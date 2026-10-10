using System.Net.Http.Headers;

// Gateway: єдина точка входу для зовнішніх клієнтів.
// Клієнт звертається лише до /api/..., не знаючи, який сервіс відповідає.
// Внутрішні ендпоінти /internal/* шлюз не пропускає.

var builder = WebApplication.CreateBuilder(args);

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

app.MapMethods("/api/{segment}/{**rest}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE" },
    async (string segment, HttpContext ctx, IHttpClientFactory factory) =>
{
    if (!upstreams.TryGetValue(segment, out var up))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new { error = "Маршрут не знайдено." });
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
