using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Sales.Service.Tests;

public enum CatalogMode { Ok, Down, Conflict }

/// <summary>Заглушка Catalog.Service: відповідає за контрактом services/contracts/catalog.openapi.yaml.</summary>
public sealed class StubCatalogHandler : HttpMessageHandler
{
    public CatalogMode Mode { get; set; } = CatalogMode.Ok;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Mode == CatalogMode.Down)
            throw new HttpRequestException("Catalog недоступний (імітація).");

        var path = request.RequestUri!.AbsolutePath;
        var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);

        if (path.EndsWith("/reservations"))
        {
            if (Mode == CatalogMode.Conflict)
                return Json(HttpStatusCode.Conflict, new { error = "Місце зайняте." });

            var items = body.GetProperty("seats").EnumerateArray()
                .Select(s => new { row = s.GetProperty("row").GetInt32(), number = s.GetProperty("number").GetInt32(), priceUah = 250m })
                .ToList();
            return Json(HttpStatusCode.Created, new
            {
                orderId = body.GetProperty("orderId").GetGuid(),
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                items
            });
        }

        if (path.EndsWith("/confirm"))
            return Json(HttpStatusCode.OK, new { orderId = body.GetProperty("orderId").GetGuid(), status = "Sold" });

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object payload) =>
        new(code) { Content = JsonContent.Create(payload) };
}

/// <summary>Заглушка Notifications: завжди приймає подію.</summary>
public sealed class AcceptingNotificationsHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { status = "accepted" }) });
}

/// <summary>
/// Характеризаційні тести: фіксують ПОТОЧНУ поведінку Sales.Service до рефакторингу (ЛР4).
/// Тести не змінюються під час рефакторингу; якщо вони падають, поведінку змінено.
/// </summary>
[TestFixture]
public class SalesCharacterizationTests
{
    private SalesFactory _factory = null!;
    private HttpClient _client = null!;

    private static string Token(string sub, string role) =>
        TokenService.Issue(sub, role, System.Text.Encoding.UTF8.GetBytes(SalesFactory.TestKey),
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15));

    /// <summary>Підставляє токен покупця для всіх наступних запитів клієнта.</summary>
    private void LoginAsBuyer(string email) =>
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token(email, "buyer"));

    /// <summary>Клієнт із токеном контролера для перевірки квитків на вході.</summary>
    private HttpClient ControllerClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token("gate@example.com", "controller"));
        return c;
    }

    [SetUp]
    public void SetUp()
    {
        _factory = new SalesFactory();
        _client = _factory.CreateClient();
        LoginAsBuyer("guest@example.com");
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private StubCatalogHandler Catalog => _factory.Catalog;

    private static object OrderRequest(string email, int seats = 2) => new
    {
        sessionId = Guid.NewGuid(),
        buyerEmail = email,
        seats = Enumerable.Range(1, seats).Select(n => new { row = 1, number = n }).ToArray()
    };

    private async Task<(Guid OrderId, HttpResponseMessage Response)> PlaceOrder(string email, int seats = 2)
    {
        var response = await _client.PostAsJsonAsync("/api/orders", OrderRequest(email, seats));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = response.IsSuccessStatusCode ? doc.RootElement.GetProperty("orderId").GetGuid() : Guid.Empty;
        return (id, response);
    }

    [Test]
    public async Task Placing_order_reserves_seats_and_returns_created_with_total()
    {
        var (_, response) = await PlaceOrder("guest@example.com", seats: 2);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("Created"));
        Assert.That(body.RootElement.GetProperty("total").GetDecimal(), Is.EqualTo(500m));
    }

    [Test]
    public async Task Invalid_email_is_rejected_with_bad_request()
    {
        var (_, response) = await PlaceOrder("not-an-email");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task When_catalog_is_down_order_is_not_created_and_503_names_catalog()
    {
        Catalog.Mode = CatalogMode.Down;

        var response = await _client.PostAsJsonAsync("/api/orders", OrderRequest("guest@example.com"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("service").GetString(), Is.EqualTo("catalog"));
    }

    [Test]
    public async Task When_catalog_rejects_seats_the_same_status_and_error_are_returned()
    {
        Catalog.Mode = CatalogMode.Conflict;

        var (_, response) = await PlaceOrder("guest@example.com");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("error").GetString(), Is.EqualTo("Місце зайняте."));
    }

    [Test]
    public async Task Declined_payment_returns_402_and_order_stays_unpaid()
    {
        LoginAsBuyer("buyer@decline.test");
        var (orderId, _) = await PlaceOrder("buyer@decline.test");

        var pay = await _client.PostAsync($"/api/orders/{orderId}/payment", null);

        Assert.That(pay.StatusCode, Is.EqualTo((HttpStatusCode)402));
        using var order = JsonDocument.Parse(await _client.GetStringAsync($"/api/orders/{orderId}"));
        Assert.That(order.RootElement.GetProperty("status").GetString(), Is.EqualTo("Created"));
    }

    [Test]
    public async Task Successful_payment_issues_one_ticket_per_seat()
    {
        var (orderId, _) = await PlaceOrder("guest@example.com", seats: 2);

        var pay = await _client.PostAsync($"/api/orders/{orderId}/payment", null);

        Assert.That(pay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var body = JsonDocument.Parse(await pay.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("Paid"));
        Assert.That(body.RootElement.GetProperty("ticketCodes").GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task Paying_the_same_order_twice_is_rejected_with_conflict()
    {
        var (orderId, _) = await PlaceOrder("guest@example.com");
        await _client.PostAsync($"/api/orders/{orderId}/payment", null);

        var second = await _client.PostAsync($"/api/orders/{orderId}/payment", null);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task Ticket_is_accepted_once_and_then_rejected()
    {
        var (orderId, _) = await PlaceOrder("guest@example.com", seats: 1);
        using var paid = JsonDocument.Parse(await (await _client.PostAsync($"/api/orders/{orderId}/payment", null)).Content.ReadAsStringAsync());
        var code = paid.RootElement.GetProperty("ticketCodes")[0].GetString();

        var gate = ControllerClient();
        using var first = JsonDocument.Parse(await (await gate.PostAsJsonAsync("/api/tickets/validation", new { code })).Content.ReadAsStringAsync());
        using var second = JsonDocument.Parse(await (await gate.PostAsJsonAsync("/api/tickets/validation", new { code })).Content.ReadAsStringAsync());

        Assert.That(first.RootElement.GetProperty("accepted").GetBoolean(), Is.True);
        Assert.That(second.RootElement.GetProperty("accepted").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Unknown_ticket_code_is_rejected_without_error()
    {
        using var body = JsonDocument.Parse(await (await ControllerClient().PostAsJsonAsync("/api/tickets/validation", new { code = "NOPE" })).Content.ReadAsStringAsync());

        Assert.That(body.RootElement.GetProperty("accepted").GetBoolean(), Is.False);
        Assert.That(body.RootElement.GetProperty("reason").GetString(), Is.EqualTo("Квиток не знайдено."));
    }

    [Test]
    public async Task Placing_order_without_token_is_unauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.PostAsJsonAsync("/api/orders", OrderRequest("guest@example.com"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Buyer_cannot_pay_another_buyers_order()
    {
        LoginAsBuyer("alice@example.com");
        var (orderId, _) = await PlaceOrder("alice@example.com");

        LoginAsBuyer("bob@example.com");
        var pay = await _client.PostAsync($"/api/orders/{orderId}/payment", null);

        Assert.That(pay.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Buyer_cannot_order_tickets_to_another_email()
    {
        LoginAsBuyer("alice@example.com");
        var response = await _client.PostAsJsonAsync("/api/orders", OrderRequest("bob@example.com"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Buyer_cannot_validate_tickets()
    {
        var response = await _client.PostAsJsonAsync("/api/tickets/validation", new { code = "X" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}

public sealed class SalesFactory : WebApplicationFactory<Program>
{
    public const string TestKey = "test-signing-key-only-for-tests-0123456789";
    public StubCatalogHandler Catalog { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Key", TestKey);
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<CatalogClient>().ConfigurePrimaryHttpMessageHandler(() => Catalog);
            services.AddHttpClient("notifications").ConfigurePrimaryHttpMessageHandler(() => new AcceptingNotificationsHandler());
        });
    }
}
