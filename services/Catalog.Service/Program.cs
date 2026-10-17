using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;

// Catalog.Service: контекст «Каталог» (заходи, сеанси, схеми залів, місця).
// Володіє даними Event, Session, Seat. Інші сервіси до них не звертаються напряму.
// ЛР6: створювати заходи може організатор; сеанси — лише власник заходу; внутрішні ендпоінти — лише службовий токен.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMemoryCache();
var authKey = TokenService.RequireKey(builder.Configuration);
var app = builder.Build();

var events = new Dictionary<Guid, Event>();
var eventOwners = new Dictionary<Guid, string>();   // власник заходу (sub токена організатора)
var sessions = new Dictionary<Guid, Session>();
var gate = new object();

// Кеш читання схеми залу (ЛР5). Ключ: sessionId. Життя запису: 5 хв.
// Інвалідизація: резервування, звільнення та підтвердження продажу цього сеансу.
// Вимикається прапорцем Catalog:SeatsCache:Enabled=false (для замірів «без кешу», ЛР6).
var seatsCacheEnabled = builder.Configuration.GetValue("Catalog:SeatsCache:Enabled", true);
var seatsCacheTtl = TimeSpan.FromMinutes(5);

app.MapGet("/health", () => Results.Ok(new { service = "catalog", status = "ok" }));

// Публічні ендпоінти каталогу (через шлюз). Створення — лише для організатора
app.MapPost("/api/events", (CreateEventRequest r, HttpRequest http) => Domain(() =>
{
    if (TokenService.RequireRole(http, authKey, "organizer", out var who) is { } denied) return denied;
    var ev = Event.Create(r.Title, r.OrganizerName);
    ev.Publish();
    lock (gate)
    {
        events[ev.Id] = ev;
        eventOwners[ev.Id] = who.UserId;
    }
    return Results.Created($"/api/events/{ev.Id}", new { eventId = ev.Id, status = ev.Status.ToString() });
}));

app.MapPost("/api/sessions", (CreateSessionRequest r, HttpRequest http) => Domain(() =>
{
    if (TokenService.RequireRole(http, authKey, "organizer", out var who) is { } denied) return denied;
    lock (gate)
    {
        if (!events.TryGetValue(r.EventId, out var ev))
            return Results.NotFound(new { error = "Захід не знайдено." });
        // Авторизація на рівні власника ресурсу: чужий захід змінювати не можна
        if (eventOwners[r.EventId] != who.UserId)
            return Results.Json(new { error = "Це не ваш захід." }, statusCode: StatusCodes.Status403Forbidden);
        ev.EnsureOnSale();
        var layout = r.Seats.Select(s => (new SeatCode(s.Row, s.Number), Money.Uah(s.PriceUah)));
        var session = Session.Schedule(ev.Id, r.VenueName, r.StartsAt, layout, Now());
        session.OpenSales(Now());
        sessions[session.Id] = session;
        return Results.Created($"/api/sessions/{session.Id}",
            new { sessionId = session.Id, status = session.Status.ToString() });
    }
}));

// Схема залу: публічна. Заголовок Server-Timing показує, де витрачається час (ЛР6, пошук вузького місця)
app.MapGet("/api/sessions/{id:guid}/seats", (Guid id, IMemoryCache cache, HttpResponse resp) => Domain(() =>
{
    var now = Now();
    SeatMapSnapshot? snap = null;
    if (seatsCacheEnabled && cache.TryGetValue(SeatsKey(id), out SeatMapSnapshot? cached))
        snap = cached;

    double lockMs = 0, buildMs = 0;
    if (snap is null)
    {
        var tLock = Stopwatch.GetTimestamp();
        lock (gate)
        {
            lockMs = ElapsedMs(tLock);
            if (!sessions.TryGetValue(id, out var s))
                return Results.NotFound(new { error = "Сеанс не знайдено." });
            var tBuild = Stopwatch.GetTimestamp();
            snap = Snapshot(s);
            buildMs = ElapsedMs(tBuild);
            // Запис у кеш під тим самим замком, що й інвалідизація
            if (seatsCacheEnabled) cache.Set(SeatsKey(id), snap, seatsCacheTtl);
        }
    }

    var tJson = Stopwatch.GetTimestamp();
    var json = JsonSerializer.Serialize(SeatMapResponse(snap!, now));
    var jsonMs = ElapsedMs(tJson);
    resp.Headers["Server-Timing"] =
        $"lock;dur={Ms(lockMs)}, build;dur={Ms(buildMs)}, json;dur={Ms(jsonMs)}";
    return Results.Content(json, "application/json");
}));

// Внутрішній синхронний ендпоінт: викликає Sales.Service при створенні замовлення.
// Не проксіюється шлюзом. ЛР6: приймає лише службовий токен (роль service).
app.MapPost("/internal/sessions/{id:guid}/reservations", (Guid id, ReserveRequest r, HttpRequest http, IMemoryCache cache) => Domain(() =>
{
    if (TokenService.RequireRole(http, authKey, "service", out _) is { } denied) return denied;
    lock (gate)
    {
        try
        {
            if (!sessions.TryGetValue(id, out var s))
                return Results.NotFound(new { error = "Сеанс не знайдено." });
            var codes = r.Seats.Select(x => new SeatCode(x.Row, x.Number)).ToList();
            var now = Now();
            var reserved = s.ReserveSeats(r.OrderId, codes, now);
            return Results.Created($"/internal/sessions/{id}/reservations/{r.OrderId}", new
            {
                orderId = r.OrderId,
                expiresAt = now + Session.ReservationDuration,
                items = reserved.Select(x => new { row = x.Code.Row, number = x.Code.Number, priceUah = x.Price.Amount })
            });
        }
        finally
        {
            // Інвалідизація за подією зміни: навіть при відмові сеанс міг змінитися частково
            cache.Remove(SeatsKey(id));
        }
    }
}));

// Внутрішній синхронний ендпоінт: підтверджує продаж місць після оплати. Лише службовий токен.
app.MapPost("/internal/sessions/{id:guid}/confirm", (Guid id, ConfirmRequest r, HttpRequest http, IMemoryCache cache) => Domain(() =>
{
    if (TokenService.RequireRole(http, authKey, "service", out _) is { } denied) return denied;
    lock (gate)
    {
        try
        {
            if (!sessions.TryGetValue(id, out var s))
                return Results.NotFound(new { error = "Сеанс не знайдено." });
            s.ConfirmSale(r.OrderId, Now());
            return Results.Ok(new { orderId = r.OrderId, status = "Sold" });
        }
        finally
        {
            cache.Remove(SeatsKey(id));
        }
    }
}));

app.Run();

// Порушення доменного правила -> 409 (як у ЛР2, табл. 4)
static IResult Domain(Func<IResult> action)
{
    try { return action(); }
    catch (DomainException ex) { return Results.Conflict(new { error = ex.Message }); }
}

static DateTimeOffset Now() => DateTimeOffset.UtcNow;

static string SeatsKey(Guid sessionId) => $"seats:{sessionId}";

static double ElapsedMs(long startTimestamp) =>
    (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

static string Ms(double ms) => ms.ToString("F3", CultureInfo.InvariantCulture);

// Знімок стану сеансу для кешу. Статус «Available» для місця з простроченим резервуванням
// обчислюється при читанні, тож кешований знімок не застаріває з часом.
static SeatMapSnapshot Snapshot(Session s) => new(
    s.Id,
    s.Status.ToString(),
    s.Seats
        .OrderBy(x => x.Code.Row).ThenBy(x => x.Code.Number)
        .Select(x => new SeatView(x.Code.Row, x.Code.Number, x.Price.Amount, x.Status.ToString(), x.ReservedUntil))
        .ToList());

static object SeatMapResponse(SeatMapSnapshot snap, DateTimeOffset now) => new
{
    sessionId = snap.SessionId,
    status = snap.SessionStatus,
    seats = snap.Seats.Select(v => new
    {
        row = v.Row,
        number = v.Number,
        priceUah = v.PriceUah,
        status = IsAvailable(v, now) ? "Available" : v.Status
    })
};

static bool IsAvailable(SeatView v, DateTimeOffset now) =>
    v.Status == "Available" || (v.Status == "Reserved" && v.ReservedUntil <= now);

public sealed record SeatView(int Row, int Number, decimal PriceUah, string Status, DateTimeOffset? ReservedUntil);
public sealed record SeatMapSnapshot(Guid SessionId, string SessionStatus, IReadOnlyList<SeatView> Seats);

public sealed record CreateEventRequest(string Title, string OrganizerName);
public sealed record CreateSessionRequest(Guid EventId, string VenueName, DateTimeOffset StartsAt, List<SeatPrice> Seats);
public sealed record SeatPrice(int Row, int Number, decimal PriceUah);
public sealed record SeatRef(int Row, int Number);
public sealed record ReserveRequest(Guid OrderId, List<SeatRef> Seats);
public sealed record ConfirmRequest(Guid OrderId);

// Потрібно для тестів (WebApplicationFactory). Не впливає на роботу сервісу.
public partial class Program { }
