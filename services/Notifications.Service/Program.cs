// Notifications.Service: споживач події TicketsIssued (асинхронна взаємодія).
// Власних даних, які потрібні іншим сервісам, не має. Продавець події не залежить від цього сервісу.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var gate = new object();
var seen = new HashSet<Guid>();   // ідемпотентність: eventId уже оброблених подій
var inbox = new List<object>();   // «надіслані» листи (імітація провайдера e-mail)

app.MapGet("/health", () => Results.Ok(new { service = "notifications", status = "ok" }));

// Ендпоінт, на який Sales.Service доставляє подію. Повторна доставка не створює дубля.
app.MapPost("/events", (TicketsIssuedEvent evt) =>
{
    lock (gate)
    {
        if (!seen.Add(evt.EventId))
            return Results.Ok(new { status = "duplicate" });

        inbox.Add(new
        {
            eventId = evt.EventId,
            orderId = evt.Payload.OrderId,
            to = evt.Payload.BuyerEmail,
            subject = "Ваші електронні квитки",
            ticketCount = evt.Payload.TicketCodes.Count,
            sentAt = DateTimeOffset.UtcNow
        });
    }
    return Results.Ok(new { status = "accepted" });
});

app.MapGet("/api/notifications", () =>
{
    lock (gate)
        return Results.Ok(new { count = inbox.Count, items = inbox.ToList() });
});

app.Run();

public sealed record TicketsIssuedEvent(Guid EventId, string Type, DateTimeOffset OccurredAt, TicketsIssuedPayload Payload);
public sealed record TicketsIssuedPayload(Guid OrderId, Guid SessionId, string BuyerEmail, List<string> TicketCodes);
