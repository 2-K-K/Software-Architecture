using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

// ЛР7, контрактний тест ПОСТАЧАЛЬНИКА (Catalog). Перевіряє, що реальна відповідь Catalog
// відповідає тому самому файлу контракту, за яким тестує споживач (Sales).
[TestFixture]
public class CatalogContractProviderTests
{
    private const string TestKey = "contract-test-signing-key-0123456789abcdef";
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private Guid _sessionId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Auth:Key", TestKey));
        _client = _factory.CreateClient();

        var org = Token("org@example.com", "organizer");
        var ev = await PostJson("/api/events", new { title = "Контракт", organizerName = "Тест" }, org);
        var eventId = ev.GetProperty("eventId").GetGuid();
        var se = await PostJson("/api/sessions", new
        {
            eventId,
            venueName = "Зал",
            startsAt = "2030-12-01T18:00:00Z",
            seats = new[] { new { row = 1, number = 1, priceUah = 250 }, new { row = 1, number = 2, priceUah = 250 } }
        }, org);
        _sessionId = se.GetProperty("sessionId").GetGuid();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task Provider_answers_201_with_the_fields_declared_in_the_contract()
    {
        var contract = JsonDocument.Parse(File.ReadAllText(FindContract())).RootElement;
        var required = contract.GetProperty("responses").GetProperty("201").GetProperty("required");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/sessions/{_sessionId}/reservations")
        {
            Content = JsonContent.Create(new { orderId = Guid.NewGuid(), seats = new[] { new { row = 1, number = 1 } } })
        };
        request.Headers.Authorization = new("Bearer", Token("sales", "service"));

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(body.TryGetProperty("orderId", out _) && body.TryGetProperty("expiresAt", out _), Is.True);
        Assert.That(body.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
        var item = body.GetProperty("items")[0];
        Assert.That(item.TryGetProperty("row", out _) && item.TryGetProperty("number", out _)
                    && item.TryGetProperty("priceUah", out _), Is.True, "у items немає полів контракту");
        Assert.That(required.TryGetProperty("items", out _), Is.True);
    }

    [Test]
    public async Task Provider_answers_404_with_error_text_for_unknown_session()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/sessions/{Guid.NewGuid()}/reservations")
        {
            Content = JsonContent.Create(new { orderId = Guid.NewGuid(), seats = new[] { new { row = 1, number = 1 } } })
        };
        request.Headers.Authorization = new("Bearer", Token("sales", "service"));

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(body.TryGetProperty("error", out _), Is.True);
    }

    private static string Token(string sub, string role) =>
        TokenService.Issue(sub, role, System.Text.Encoding.UTF8.GetBytes(TestKey),
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15));

    private async Task<JsonElement> PostJson(string url, object body, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new("Bearer", token);
        var res = await _client.SendAsync(req);
        Assert.That((int)res.StatusCode, Is.EqualTo(201), $"{url}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string FindContract()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "contracts", "sales-catalog-reserve.json")))
            dir = dir.Parent;
        if (dir == null) throw new FileNotFoundException("contracts/sales-catalog-reserve.json не знайдено.");
        return Path.Combine(dir.FullName, "contracts", "sales-catalog-reserve.json");
    }
}
