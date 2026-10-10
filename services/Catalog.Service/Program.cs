using Ticketing.Domain.Entities;
using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;

// Catalog.Service: контекст «Каталог» (заходи, сеанси, схеми залів, місця).
// Володіє даними Event, Session, Seat. Інші сервіси до них не звертаються напряму.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var events = new Dictionary<Guid, Event>();
var sessions = new Dictionary<Guid, Session>();
var gate = new object();

app.MapGet("/health", () => Results.Ok(new { service = "catalog", status = "ok" }));

// Публічні ендпоінти каталогу (через шлюз)
app.MapPost("/api/events", (CreateEventRequest r) => Domain(() =>
{
    var ev = Event.Create(r.Title, r.OrganizerName);
    ev.Publish();
    lock (gate) events[ev.Id] = ev;
    return Results.Created($"/api/events/{ev.Id}", new { eventId = ev.Id, status = ev.Status.ToString() });
}));

app.MapPost("/api/sessions", (CreateSessionRequest r) => Domain(() =>
{
    lock (gate)
    {
        if (!events.TryGetValue(r.EventId, out var ev))
            return Results.NotFound(new { error = "Захід не знайдено." });
        ev.EnsureOnSale();
        var layout = r.Seats.Select(s => (new SeatCode(s.Row, s.Number), Money.Uah(s.PriceUah)));
        var session = Session.Schedule(ev.Id, r.VenueName, r.StartsAt, layout, Now());
        session.OpenSales(Now());
        sessions[session.Id] = session;
        return Results.Created($"/api/sessions/{session.Id}",
            new { sessionId = session.Id, status = session.Status.ToString() });
    }
}));

app.MapGet("/api/sessions/{id:guid}/seats", (Guid id) => Domain(() =>
{
    lock (gate)
    {
        if (!sessions.TryGetValue(id, out var s))
            return Results.NotFound(new { error = "Сеанс не знайдено." });
        var now = Now();
        var seats = s.Seats
            .OrderBy(x => x.Code.Row).ThenBy(x => x.Code.Number)
            .Select(x => new
            {
                row = x.Code.Row,
                number = x.Code.Number,
                priceUah = x.Price.Amount,
                // Місце з простроченим резервуванням показуємо як вільне
                status = x.IsAvailableAt(now) ? "Available" : x.Status.ToString()
            });
        return Results.Ok(new { sessionId = s.Id, status = s.Status.ToString(), seats });
    }
}));

// Внутрішній синхронний ендпоінт: викликає Sales.Service при створенні замовлення.
// Не проксіюється шлюзом.
app.MapPost("/internal/sessions/{id:guid}/reservations", (Guid id, ReserveRequest r) => Domain(() =>
{
    lock (gate)
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
}));

// Внутрішній синхронний ендпоінт: підтверджує продаж місць після оплати.
app.MapPost("/internal/sessions/{id:guid}/confirm", (Guid id, ConfirmRequest r) => Domain(() =>
{
    lock (gate)
    {
        if (!sessions.TryGetValue(id, out var s))
            return Results.NotFound(new { error = "Сеанс не знайдено." });
        s.ConfirmSale(r.OrderId, Now());
        return Results.Ok(new { orderId = r.OrderId, status = "Sold" });
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

public sealed record CreateEventRequest(string Title, string OrganizerName);
public sealed record CreateSessionRequest(Guid EventId, string VenueName, DateTimeOffset StartsAt, List<SeatPrice> Seats);
public sealed record SeatPrice(int Row, int Number, decimal PriceUah);
public sealed record SeatRef(int Row, int Number);
public sealed record ReserveRequest(Guid OrderId, List<SeatRef> Seats);
public sealed record ConfirmRequest(Guid OrderId);
