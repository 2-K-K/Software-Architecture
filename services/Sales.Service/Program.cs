using System.Threading.Channels;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;

// Sales.Service: контекст «Продаж» (замовлення, оплата, квитки, валідація).
// Володіє даними Order і Ticket. Місця належать Catalog.Service і змінюються лише через його API.

var builder = WebApplication.CreateBuilder(args);

var catalogUrl = builder.Configuration["Upstreams:Catalog"] ?? "http://localhost:5001";
var notificationsUrl = builder.Configuration["Upstreams:Notifications"] ?? "http://localhost:5003";

builder.Services.AddSingleton<SalesStore>();
builder.Services.AddSingleton(Channel.CreateUnbounded<TicketsIssuedEvent>());
builder.Services.AddSingleton<OrderFulfillment>();

// Синхронний виклик із тайм-аутом 2 с: якщо Catalog мовчить, відповідаємо 503, а не чекаємо далі
builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri(catalogUrl))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(2));

// Асинхронна доставка: окремий клієнт із власним тайм-аутом
builder.Services.AddHttpClient("notifications", c => c.BaseAddress = new Uri(notificationsUrl))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(3));

builder.Services.AddHostedService<TicketEventDispatcher>();
builder.Services.AddHostedService<PendingConfirmationWorker>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "sales", status = "ok" }));

// Створення замовлення: синхронне резервування в Catalog з тайм-аутом
app.MapPost("/api/orders", async (PlaceOrderRequest r, CatalogClient catalog, SalesStore store, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(r.BuyerEmail) || !r.BuyerEmail.Contains('@'))
        return Results.BadRequest(new { error = "Вкажіть коректну адресу e-mail." });
    if (r.Seats is null || r.Seats.Count is < 1 or > Session.MaxSeatsPerOrder)
        return Results.BadRequest(new { error = "Замовлення має містити від 1 до 6 місць." });

    var orderId = Guid.NewGuid();
    CatalogCall<ReservationResponse> call;
    try
    {
        call = await catalog.ReserveAsync(r.SessionId, orderId, r.Seats, ct);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        // Відмова синхронної залежності: замовлення не створюється, клієнт може повторити запит
        return Results.Json(new { error = "Каталог тимчасово недоступний. Спробуйте пізніше.", service = "catalog" },
            statusCode: 503);
    }

    if (!call.Ok)
        return Results.Json(new { error = call.Error }, statusCode: call.StatusCode);

    var items = call.Value!.Items
        .Select(i => new ReservedSeat(new SeatCode(i.Row, i.Number), Money.Uah(i.PriceUah)))
        .ToList();
    var order = Order.Place(orderId, r.SessionId, r.BuyerEmail, items, call.Value.ExpiresAt);
    store.Add(order);

    return Results.Created($"/api/orders/{orderId}", new
    {
        orderId,
        status = order.Status.ToString(),
        expiresAt = call.Value.ExpiresAt,
        total = order.Total.Amount
    });
});

app.MapGet("/api/orders/{id:guid}", (Guid id, SalesStore store) =>
{
    var order = store.Find(id);
    if (order is null)
        return Results.NotFound(new { error = "Замовлення не знайдено." });
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

// Оплата: перевірка стану, списання (імітація шлюзу), підтвердження в Catalog, видача квитків
app.MapPost("/api/orders/{id:guid}/payment", async (Guid id, SalesStore store, OrderFulfillment fulfillment, CancellationToken ct) =>
{
    var order = store.Find(id);
    if (order is null)
        return Results.NotFound(new { error = "Замовлення не знайдено." });

    try
    {
        order.EnsurePayable(DateTimeOffset.UtcNow);
    }
    catch (DomainException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }

    // Імітація платіжного шлюзу: картки з доменом @decline.test відхиляються
    if (order.BuyerEmail.EndsWith("@decline.test", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { error = "Платіж відхилено банком." }, statusCode: 402);

    try
    {
        lock (store.Gate)
            order.MarkPaid("PAY-" + Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow);
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

// Перевірка квитка на вході: одноразове використання
app.MapPost("/api/tickets/validation", (ValidateTicketRequest r, SalesStore store) =>
{
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
