using System.Net;
using System.Text.Json;
using NUnit.Framework;

// ЛР7, контрактний тест СПОЖИВАЧА (Sales). Виконується без запущеного Catalog:
// відповідь подається з контракту через підставний HttpMessageHandler.
[TestFixture]
public class CatalogContractConsumerTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Contract()
    {
        var path = ContractFile.Find();
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    [Test]
    public void Consumer_parses_the_contract_response_into_its_model()
    {
        var example = Contract().GetProperty("example").GetProperty("response201").GetRawText();

        var model = JsonSerializer.Deserialize<ReservationResponse>(example, Web);

        Assert.That(model, Is.Not.Null);
        Assert.That(model!.Items, Has.Count.EqualTo(2));
        Assert.That(model.Items[0].PriceUah, Is.EqualTo(250m));
        Assert.That(model.ExpiresAt, Is.Not.EqualTo(default(DateTimeOffset)));
    }

    [Test]
    public void Consumer_sends_the_request_shape_declared_in_the_contract()
    {
        var expected = Contract().GetProperty("request").GetProperty("body");
        var seats = new List<SeatRef> { new(1, 1), new(1, 2) };

        var json = JsonSerializer.Serialize(new ReserveRequest(Guid.NewGuid(), seats), Web);
        using var doc = JsonDocument.Parse(json);

        Assert.That(doc.RootElement.TryGetProperty("orderId", out _), Is.True, "немає orderId");
        Assert.That(doc.RootElement.TryGetProperty("seats", out var sent), Is.True, "немає seats");
        Assert.That(sent[0].TryGetProperty("row", out _) && sent[0].TryGetProperty("number", out _), Is.True);
        Assert.That(expected.TryGetProperty("seats", out _), Is.True, "у контракті немає seats");
    }

    [Test]
    public async Task CatalogClient_returns_success_for_a_contract_compliant_response()
    {
        var body = Contract().GetProperty("example").GetProperty("response201").GetRawText();
        var handler = new StubHandler(HttpStatusCode.Created, body);
        var client = new CatalogClient(new HttpClient(handler) { BaseAddress = new Uri("http://stub") });

        var call = await client.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(),
            new List<SeatRef> { new(1, 1) }, CancellationToken.None);

        Assert.That(call.Ok, Is.True);
        Assert.That(call.Value!.Items, Has.Count.EqualTo(2));
    }

    private sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }
}

/// <summary>Знаходить файл контракту в корені репозиторію (спільне місце для споживача і постачальника).</summary>
public static class ContractFile
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "contracts", "sales-catalog-reserve.json")))
            dir = dir.Parent;
        if (dir == null) throw new FileNotFoundException("contracts/sales-catalog-reserve.json не знайдено.");
        return Path.Combine(dir.FullName, "contracts", "sales-catalog-reserve.json");
    }
}
