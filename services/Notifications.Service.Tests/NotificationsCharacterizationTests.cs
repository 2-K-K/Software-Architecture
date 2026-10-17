using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace Notifications.Service.Tests;

/// <summary>
/// Характеризаційні тести: фіксують ПОТОЧНУ поведінку Notifications.Service до рефакторингу (ЛР4).
/// Тести не змінюються під час рефакторингу; якщо вони падають, поведінку змінено.
/// </summary>
[TestFixture]
public class NotificationsCharacterizationTests
{
    private const string Key = "test-signing-key-only-for-tests-0123456789";
    private WebApplicationFactory<Program> _factory = null!;

    private static string Token(string sub, string role) =>
        TokenService.Issue(sub, role, System.Text.Encoding.UTF8.GetBytes(Key), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15));

    // ЛР6: подію надсилає лише Sales (службовий токен)
    private Task<HttpResponseMessage> PostEvent(object evt)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/events") { Content = JsonContent.Create(evt) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token("sales", "service"));
        return _client.SendAsync(req);
    }

    // ЛР6: список сповіщень бачить лише адміністратор
    private async Task<string> GetList()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/notifications");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token("admin@example.com", "admin"));
        var response = await _client.SendAsync(req);
        return await response.Content.ReadAsStringAsync();
    }
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        // Окрема in-memory база на кожен тест, щоб тести не впливали одне на одного
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Notifications:Db", $"Data Source=test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared")
             .UseSetting("Auth:Key", Key));
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static object TicketsIssued(Guid eventId) => new
    {
        eventId,
        type = "TicketsIssued",
        occurredAt = DateTimeOffset.UtcNow,
        payload = new
        {
            orderId = Guid.NewGuid(),
            sessionId = Guid.NewGuid(),
            buyerEmail = "buyer@example.com",
            ticketCodes = new[] { "ABC123" }
        }
    };

    [Test]
    public async Task Empty_inbox_reports_zero_notifications()
    {
        using var doc = JsonDocument.Parse(await GetList());
        Assert.That(doc.RootElement.GetProperty("count").GetInt32(), Is.EqualTo(0));
    }

    [Test]
    public async Task New_event_is_accepted_and_recorded_with_buyer_and_ticket_count()
    {
        var response = await PostEvent(TicketsIssued(Guid.NewGuid()));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("accepted"));

        using var list = JsonDocument.Parse(await GetList());
        Assert.That(list.RootElement.GetProperty("count").GetInt32(), Is.EqualTo(1));
        var item = list.RootElement.GetProperty("items")[0];
        Assert.That(item.GetProperty("to").GetString(), Is.EqualTo("buyer@example.com"));
        Assert.That(item.GetProperty("ticketCount").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task Redelivered_event_is_reported_as_duplicate_and_recorded_once()
    {
        var evt = TicketsIssued(Guid.NewGuid());
        await PostEvent(evt);

        var second = await PostEvent(evt);

        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("duplicate"));
        using var list = JsonDocument.Parse(await GetList());
        Assert.That(list.RootElement.GetProperty("count").GetInt32(), Is.EqualTo(1));
    }
}
