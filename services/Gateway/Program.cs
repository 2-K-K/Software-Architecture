using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

// Gateway: єдина точка входу для зовнішніх клієнтів.
// Клієнт звертається лише до /api/..., не знаючи, який сервіс відповідає.
// Внутрішні ендпоінти /internal/* шлюз не пропускає.
// ЛР6: тут видається токен (вхід), а на вході перевіряється підпис і термін дії.
// Права доступу перевіряють самі сервіси, бо обхід шлюзу не має давати доступу.

var builder = WebApplication.CreateBuilder(args);

var authKey = TokenService.RequireKey(builder.Configuration);
var demoPassword = builder.Configuration["Auth:DemoPassword"]
    ?? throw new InvalidOperationException("Задайте пароль демо-акаунтів у змінній середовища Auth__DemoPassword.");

// Демо-акаунти для лабораторної. Логін = sub у токені. Паролі не зберігаються в коді.
var accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["org@example.com"] = "organizer",
    ["org2@example.com"] = "organizer",
    ["alice@example.com"] = "buyer",
    ["bob@example.com"] = "buyer",
    ["gate@example.com"] = "controller",
    ["admin@example.com"] = "admin",
};

// Тайм-аут на рівні шлюзу: якщо сервіс не відповів, клієнт отримає 503, а не чекатиме
builder.Services.AddHttpClient("forward")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));

var upstreams = new Dictionary<string, (string Service, string Url)>(StringComparer.OrdinalIgnoreCase)
{
    ["events"] = ("catalog", builder.Configuration["Upstreams:Catalog"] ?? "http://localhost:5001"),
    ["sessions"] = ("catalog", builder.Configuration["Upstreams:Catalog"] ?? "http://localhost:5001"),
    ["orders"] = ("sales", builder.Configuration["Upstreams:Sales"] ?? "http://localhost:5002"),
    ["tickets"] = ("sales", builder.Configuration["Upstreams:Sales"] ?? "http://localhost:5002"),
    ["notifications"] = ("notifications", builder.Configuration["Upstreams:Notifications"] ?? "http://localhost:5003"),
};

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "gateway", status = "ok" }));

// Вхід: перевірка облікових даних і видача токена з терміном дії 15 хв
app.MapPost("/api/auth/login", (LoginRequest r) =>
{
    if (string.IsNullOrWhiteSpace(r.Login) || !accounts.TryGetValue(r.Login, out var role) || !SecureEquals(r.Password, demoPassword))
        return Results.Json(new { error = "Невірний логін або пароль." }, statusCode: StatusCodes.Status401Unauthorized);

    var sub = r.Login.ToLowerInvariant();
    var token = TokenService.Issue(sub, role, authKey, DateTimeOffset.UtcNow, TokenService.DefaultLifetime);
    return Results.Ok(new { token, role, expiresInMinutes = (int)TokenService.DefaultLifetime.TotalMinutes });
});

app.MapMethods("/api/{segment}/{**rest}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE" },
    async (string segment, HttpContext ctx, IHttpClientFactory factory) =>
{
    if (!upstreams.TryGetValue(segment, out var up))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new { error = "Маршрут не знайдено." });
        return;
    }

    // Публічний перегляд схеми залу; решта вимагає дійсного токена
    var isPublic = ctx.Request.Method == "GET" && PublicSeatMap.Pattern.IsMatch(ctx.Request.Path.Value ?? "");
    if (!isPublic && TokenService.FromRequest(ctx.Request, authKey, DateTimeOffset.UtcNow) is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Потрібен дійсний токен доступу." });
        return;
    }

    using var body = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(body);

    using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method),
        up.Url + ctx.Request.Path + ctx.Request.QueryString);
    if (body.Length > 0)
    {
        request.Content = new ByteArrayContent(body.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ctx.Request.ContentType ?? "application/json");
    }
    // Токен передаємо далі: сервіс перевіряє його сам
    if (ctx.Request.Headers.Authorization.Count > 0)
        request.Headers.TryAddWithoutValidation("Authorization", ctx.Request.Headers.Authorization.ToString());

    try
    {
        using var response = await factory.CreateClient("forward").SendAsync(request, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await response.Content.CopyToAsync(ctx.Response.Body);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsJsonAsync(new { error = $"Сервіс «{up.Service}» недоступний.", service = up.Service });
    }
});

app.Run();

static bool SecureEquals(string? given, string expected)
{
    if (given is null) return false;
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
}

public sealed record LoginRequest(string? Login, string? Password);

// Публічний маршрут: GET /api/sessions/{id}/seats (схема залу для перегляду без входу)
public static class PublicSeatMap
{
    public static readonly System.Text.RegularExpressions.Regex Pattern =
        new(@"^/api/sessions/[^/]+/seats$", System.Text.RegularExpressions.RegexOptions.Compiled);
}

// Потрібно для тестів (WebApplicationFactory). Не впливає на роботу сервісу.
public partial class Program { }
