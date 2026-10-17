// Notifications.Service: споживач події TicketsIssued (асинхронна взаємодія).
// Власних даних, які потрібні іншим сервісам, не має. Продавець події не залежить від цього сервісу.

var builder = WebApplication.CreateBuilder(args);
var authKey = TokenService.RequireKey(builder.Configuration);   // ЛР6: ключ перевірки токенів

// Файл за замовчуванням; тести підставляють окрему in-memory базу
var connectionString = builder.Configuration["Notifications:Db"] ?? "Data Source=notifications.db";
var db = new NotificationsDb(connectionString);
// Цільова версія схеми: 2 – етап розширення, 3 – етап скорочення (за замовчуванням)
var schemaTarget = builder.Configuration.GetValue("Notifications:SchemaTarget", Migrations.Latest);
db.Migrate(schemaTarget);
builder.Services.AddSingleton(db);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "notifications", status = "ok" }));

// Ендпоінт, на який Sales.Service доставляє подію. Повторна доставка не створює дубля
// (ідемпотентність за eventId, див. NotificationsDb.TryRecord).
app.MapPost("/events", (TicketsIssuedEvent evt, HttpRequest http, NotificationsDb store) =>
{
    // ЛР6: подію може надіслати лише Sales (службовий токен), інакше будь-хто підробить листи
    if (TokenService.RequireRole(http, authKey, "service", out _) is { } denied) return denied;
    return store.TryRecord(evt)
        ? Results.Ok(new { status = "accepted" })
        : Results.Ok(new { status = "duplicate" });
});

app.MapGet("/api/notifications", (HttpRequest http, NotificationsDb store) =>
{
    // ЛР6: у списку є адреси покупців (персональні дані) — лише для адміністратора
    if (TokenService.RequireRole(http, authKey, "admin", out _) is { } denied) return denied;
    var (count, items) = store.List();
    return Results.Ok(new { count, items });
});

app.Run();

public sealed record TicketsIssuedEvent(Guid EventId, string Type, DateTimeOffset OccurredAt, TicketsIssuedPayload Payload);
public sealed record TicketsIssuedPayload(Guid OrderId, Guid SessionId, string BuyerEmail, List<string> TicketCodes);

// Потрібно для тестів (WebApplicationFactory). Не впливає на роботу сервісу.
public partial class Program { }
