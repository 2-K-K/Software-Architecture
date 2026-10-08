using Ticketing.Domain.Exceptions;

namespace Ticketing.Domain.ValueObjects;

/// <summary>Грошова сума в гривнях. Інваріант: сума не від'ємна.</summary>
public sealed record Money
{
    public decimal Amount { get; }
    public string Currency => "UAH";

    private Money(decimal amount) => Amount = amount;

    public static Money Zero { get; } = new(0m);

    public static Money Uah(decimal amount) =>
        amount < 0 ? throw new DomainException("Сума не може бути від'ємною.") : new Money(decimal.Round(amount, 2));

    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);
}
