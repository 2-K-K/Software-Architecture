using System.Threading.Channels;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;

// Sales.Service: контекст «Продаж» (замовлення, оплата, квитки, валідація).
// Володіє даними Order і Ticket. Місця належать Catalog.Service і змінюються лише через його API.
// ЛР6: замовлення бачить і оплачує лише його власник (покупець); валідація квитків — лише контролер.

var builder = WebApplication.CreateBuilder(args);

var catalogUrl = builder.Configuration["Upstreams:Catalog"] ?? "http://localhost:5001";
var notificationsUrl = builder.Configuration["Upstreams:Notifications"] ?? "http://localhost:5003";
var authKey = TokenService.RequireKey(builder.Configuration);

builder.Services.AddSingleton<SalesStore>();
builder.Services.AddSingleton(Channel.CreateUnbounded<TicketsIssuedEvent>());
builder.Services.AddSingleton<ICatalogSeats, CatalogSeatsAdapter>();
builder.Services.AddSingleton<ITicketFactory, RandomTicketFactory>();
builder.Services.AddSingleton<OrderFulfillment>();
builder.Services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();

// Синхронний виклик із тайм-аутом 2 с: якщо Catalog мовчить, відповідаємо 503, а не чекаємо далі.
// ServiceTokenHandler додає службовий токен до кожного запиту (ЛР6).
builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri(catalogUrl))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(2))
    .AddHttpMessageHandler(() => new ServiceTokenHandler(authKey));

// Асинхронна доставка: окремий клієнт із власним тайм-аутом
builder.Services.AddHttpClient("notifications", c => c.BaseAddress = new Uri(notificationsUrl))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(3))
    .AddHttpMessageHandler(() => new ServiceTokenHandler(authKey));

builder.Services.AddSingleton<ITicketsIssuedSubscriber, NotificationsSubscriber>();
builder.Services.AddHostedService<TicketEventDispatcher>();
builder.Services.AddHostedService<PendingConfirmationWorker>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "sales", status = "ok" }));

// Створення замовлення: резервування місць через порт ICatalogSeats (Adapter).
// ЛР6: створює лише покупець, і лише на власну адресу (інакше квитки піде на чужу пошту).
app.MapPost("/api/orders", async (PlaceOrderRequest r, HttpRequest http, ICatalogSeats seats, SalesStore store, CancellationToken ct) =>
{
    if (TokenService.RequireRole(http, authKey, "buyer", out var who) is { } denied) return denied;

    if (string.IsNullOrWhiteSpace(r.BuyerEmail) || !r.BuyerEmail.Contains('@'))
        return Results.BadRequest(new { error = "Вкажіть коректну адресу e-mail." });
    if (!string.Equals(r.BuyerEmail, who.UserId, StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { error = "Квитки надсилаються лише на адресу вашого облікового запису." },
            statusCode: StatusCodes.Status403Forbidden);
    if (r.Seats is null || r.Seats.Count is < 1 or > Session.MaxSeatsPerOrder)
        return Results.BadRequest(new { error = "Замовлення має містити від 1 до 6 місць." });

    var orderId = Guid.NewGuid();
    SeatReservation reservation;
    try
    {
        var codes = r.Seats.Select(s => new SeatCode(s.Row, s.Number)).ToList();
        reservation = await seats.ReserveAsync(r.SessionId, orderId, codes, ct);
    }
    catch (DomainException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (CatalogRejectedException ex)
    {
        return Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new { error = "Каталог тимчасово недоступний. Спробуйте пізніше.", service = "catalog" },
            statusCode: 503);
    }

    var order = Order.Place(orderId, r.SessionId, r.BuyerEmail, reservation.Seats, reservation.ExpiresAt);
    store.Add(order);
    store.SetOwner(orderId, who.UserId);

    return Results.Created($"/api/orders/{orderId}", new
    {
        orderId,
        status = order.Status.ToString(),
        expiresAt = reservation.ExpiresAt,
        total = order.Total.Amount
    });
});

app.MapGet("/api/orders/{id:guid}", (Guid id, HttpRequest http, SalesStore store) =>
{
    if (TokenService.RequireRole(http, authKey, "buyer", out var who) is { } denied) return denied;
    var order = store.Find(id);
    if (order is null)
        return Results.NotFound(new { error = "Замовлення не знайдено." });
    // Авторизація на рівні власника: чуже замовлення не показуємо
    if (store.OwnerOf(id) != who.UserId)
        return Results.Json(new { error = "Це не ваше замовлення." }, statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(new
    {
        orderId = order.Id,
        status = order.Status.ToString(),
        total = order.Total.Amount,
        ticketsIssued = store.IsIssued(id),
        pendingIssue = store.IsPending(id),
        ticketCodes = store.CodesFor(id)
    });
});

// Оплата: перевірка власника і стану, списання (імітація шлюзу), підтвердження в Catalog, видача квитків
app.MapPost("/api/orders/{id:guid}/payment", async (Guid id, HttpRequest http, SalesStore store, OrderFulfillment fulfillment, IPaymentGateway gateway, CancellationToken ct) =>
{
    if (TokenService.RequireRole(http, authKey, "buyer", out var who) is { } denied) return denied;

    var order = store.Find(id);
    if (order is null)
        return Results.NotFound(new { error = "Замовлення не знайдено." });
    if (store.OwnerOf(id) != who.UserId)
        return Results.Json(new { error = "Це не ваше замовлення." }, statusCode: StatusCodes.Status403Forbidden);

    try
    {
        order.EnsurePayable(DateTimeOffset.UtcNow);
    }
    catch (DomainException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }

    var charge = await gateway.ChargeAsync(order.Id, order.Total.Amount, order.BuyerEmail, ct);
    if (!charge.Succeeded)
        return Results.Json(new { error = charge.FailureReason }, statusCode: 402);

    try
    {
        lock (store.Gate)
            order.MarkPaid(charge.Reference!, DateTimeOffset.UtcNow);
    }
    catch (DomainException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }

    var result = await fulfillment.ConfirmAndIssueAsync(id, ct);
    return result.Kind switch
    {
        FulfillKind.Issued => Results.Ok(new { orderId = id, status = "Paid", ticketCodes = store.CodesFor(id) }),
        FulfillKind.CatalogUnavailable => Results.Json(new
        {
            orderId = id,
            status = "PaidPendingIssue",
            message = "Оплату прийнято. Квитки буде видано після відновлення каталогу."
        }, statusCode: 202),
        _ => Results.Conflict(new { error = result.Error })
    };
});

// Перевірка квитка на вході: одноразове використання. Лише для контролера (ЛР6).
app.MapPost("/api/tickets/validation", (ValidateTicketRequest r, HttpRequest http, SalesStore store) =>
{
    if (TokenService.RequireRole(http, authKey, "controller", out _) is { } denied) return denied;
    var ticket = store.FindTicket(r.Code ?? string.Empty);
    if (ticket is null)
        return Results.Ok(new { accepted = false, reason = "Квиток не знайдено." });
    try
    {
        lock (store.Gate) ticket.Use();
        return Results.Ok(new { accepted = true, seat = new { row = ticket.Seat.Row, number = ticket.Seat.Number } });
    }
    catch (DomainException ex)
    {
        return Results.Ok(new { accepted = false, reason = ex.Message });
    }
});

app.Run();

// Потрібно для тестів (WebApplicationFactory). Не впливає на роботу сервісу.
public partial class Program { }
