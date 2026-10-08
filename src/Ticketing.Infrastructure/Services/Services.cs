using System.Security.Cryptography;
using System.Text;
using Ticketing.Application.Ports;
using Ticketing.Domain.ValueObjects;

namespace Ticketing.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Імітація платіжного шлюзу: відхиляє платіж лише для токена «declined».</summary>
public sealed class SimulatedPaymentGateway : IPaymentGateway
{
    public const string DeclinedToken = "declined";

    public Task<PaymentResult> ChargeAsync(Guid orderId, Money amount, string paymentToken, CancellationToken ct = default) =>
        Task.FromResult(string.Equals(paymentToken, DeclinedToken, StringComparison.OrdinalIgnoreCase)
            ? new PaymentResult(false, null, "Платіж відхилено банком-емітентом.")
            : new PaymentResult(true, $"SIM-{Guid.NewGuid():N}", null));
}

/// <summary>Код квитка з підписом HMAC-SHA256, що ускладнює підробку (сценарій якості QS-6).</summary>
public sealed class HmacTicketCodeGenerator : ITicketCodeGenerator
{
    private readonly byte[] _key;
    public HmacTicketCodeGenerator(byte[] key) => _key = key;

    public string Generate(Guid sessionId, SeatCode seat)
    {
        var payload = $"{sessionId:N}.{seat.Row}.{seat.Number}.{Guid.NewGuid():N}";
        var signature = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload));
        return $"{payload}.{Convert.ToHexString(signature, 0, 8)}";
    }
}
