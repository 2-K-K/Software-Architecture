using Ticketing.Domain.Entities;
using Ticketing.Domain.ValueObjects;

/// <summary>Результат резервування місць у термінах домену Sales.</summary>
public sealed record SeatReservation(DateTimeOffset ExpiresAt, IReadOnlyList<ReservedSeat> Seats);

/// <summary>Catalog відхилив запит (наприклад, місце зайняте). Не є збоєм зв'язку.</summary>
public sealed class CatalogRejectedException(int statusCode, string? error)
    : Exception(error ?? "Catalog відхилив запит.")
{
    public int StatusCode { get; } = statusCode;
    public string? Error { get; } = error;
}

/// <summary>
/// Порт (Adapter): Sales працює з цим інтерфейсом у доменних типах і не знає про форму відповіді Catalog.
/// Збої зв'язку виникають як HttpRequestException/TaskCanceledException, відмови Catalog — як CatalogRejectedException.
/// </summary>
public interface ICatalogSeats
{
    Task<SeatReservation> ReserveAsync(Guid sessionId, Guid orderId, IReadOnlyList<SeatCode> seats, CancellationToken ct);
    Task ConfirmAsync(Guid sessionId, Guid orderId, CancellationToken ct);
}

/// <summary>Адаптер HTTP-клієнта Catalog до порту ICatalogSeats.</summary>
public sealed class CatalogSeatsAdapter(CatalogClient client) : ICatalogSeats
{
    public async Task<SeatReservation> ReserveAsync(Guid sessionId, Guid orderId, IReadOnlyList<SeatCode> seats, CancellationToken ct)
    {
        var refs = seats.Select(s => new SeatRef(s.Row, s.Number)).ToList();
        var call = await client.ReserveAsync(sessionId, orderId, refs, ct);
        if (!call.Ok)
            throw new CatalogRejectedException(call.StatusCode, call.Error);

        var value = call.Value!;
        var reserved = value.Items
            .Select(i => new ReservedSeat(new SeatCode(i.Row, i.Number), Money.Uah(i.PriceUah)))
            .ToList();
        return new SeatReservation(value.ExpiresAt, reserved);
    }

    public async Task ConfirmAsync(Guid sessionId, Guid orderId, CancellationToken ct)
    {
        var call = await client.ConfirmAsync(sessionId, orderId, ct);
        if (!call.Ok)
            throw new CatalogRejectedException(call.StatusCode, call.Error);
    }
}
