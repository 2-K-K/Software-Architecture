/// <summary>Результат списання коштів у платіжному шлюзі.</summary>
public sealed record PaymentOutcome(bool Succeeded, string? Reference, string? FailureReason);

/// <summary>
/// Стратегія оплати. Конкретний шлюз підставляється в CompositionRoot (Program.cs),
/// тому зміна провайдера не потребує правок в ендпоінті оплати.
/// </summary>
public interface IPaymentGateway
{
    Task<PaymentOutcome> ChargeAsync(Guid orderId, decimal amount, string buyerEmail, CancellationToken ct);
}

/// <summary>Імітація шлюзу: покупці з доменом @decline.test отримують відмову.</summary>
public sealed class SimulatedPaymentGateway : IPaymentGateway
{
    public Task<PaymentOutcome> ChargeAsync(Guid orderId, decimal amount, string buyerEmail, CancellationToken ct)
    {
        if (buyerEmail.EndsWith("@decline.test", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new PaymentOutcome(false, null, "Платіж відхилено банком."));

        return Task.FromResult(new PaymentOutcome(true, "PAY-" + Guid.NewGuid().ToString("N")[..12], null));
    }
}
