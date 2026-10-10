using System.Net.Http.Json;
using System.Text.Json;

/// <summary>Результат виклику Catalog.Service: успіх, код статусу або повідомлення про помилку.</summary>
public sealed record CatalogCall<T>(bool Ok, int StatusCode, string? Error, T? Value);

public sealed record ReservationResponse(Guid OrderId, DateTimeOffset ExpiresAt, List<ReservedItem> Items);
public sealed record ReservedItem(int Row, int Number, decimal PriceUah);
public sealed record ConfirmResponse(Guid OrderId, string Status);
public sealed record ErrorBody(string? Error);

/// <summary>
/// Синхронний клієнт Catalog.Service. Тайм-аут 2 с задається в CompositionRoot (Program.cs).
/// Якщо Catalog не відповідає, HttpClient кидає HttpRequestException або TaskCanceledException;
/// їх обробляє викликаючий код і повертає клієнту 503.
/// </summary>
public sealed class CatalogClient(HttpClient http)
{
    public async Task<CatalogCall<ReservationResponse>> ReserveAsync(
        Guid sessionId, Guid orderId, IReadOnlyList<SeatRef> seats, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            $"/internal/sessions/{sessionId}/reservations", new ReserveRequest(orderId, seats.ToList()), ct);
        return await Read<ReservationResponse>(response, ct);
    }

    public async Task<CatalogCall<ConfirmResponse>> ConfirmAsync(Guid sessionId, Guid orderId, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            $"/internal/sessions/{sessionId}/confirm", new ConfirmRequest(orderId), ct);
        return await Read<ConfirmResponse>(response, ct);
    }

    private static async Task<CatalogCall<T>> Read<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        using (response)
        {
            if (response.IsSuccessStatusCode)
                return new CatalogCall<T>(true, (int)response.StatusCode, null,
                    await response.Content.ReadFromJsonAsync<T>(ct));

            string? error = null;
            try
            {
                error = (await response.Content.ReadFromJsonAsync<ErrorBody>(ct))?.Error;
            }
            catch (JsonException)
            {
                // відповідь без JSON-тіла
            }
            return new CatalogCall<T>(false, (int)response.StatusCode, error ?? response.ReasonPhrase, null);
        }
    }
}
