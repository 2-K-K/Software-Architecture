using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;

namespace Ticketing.Domain.Entities;

public enum SeatStatus { Available, Reserved, Sold }

/// <summary>
/// Місце сеансу. Змінювати стан місця можна лише через агрегат <see cref="Session"/>
/// Стани: Available -> Reserved -> Sold; Reserved -> Available при звільненні.
/// </summary>
public sealed class Seat
{
    public SeatCode Code { get; }
    public Money Price { get; }
    public SeatStatus Status { get; private set; } = SeatStatus.Available;
    public Guid? OrderId { get; private set; }
    public DateTimeOffset? ReservedUntil { get; private set; }

    internal Seat(SeatCode code, Money price)
    {
        Code = code;
        Price = price;
    }

    /// <summary>Вільне місце або таке, термін резервування якого минув.</summary>
    public bool IsAvailableAt(DateTimeOffset now) =>
        Status == SeatStatus.Available || (Status == SeatStatus.Reserved && ReservedUntil <= now);

    internal void Reserve(Guid orderId, DateTimeOffset until, DateTimeOffset now)
    {
        if (!IsAvailableAt(now))
            throw new DomainException($"Місце {Code} недоступне.");
        Status = SeatStatus.Reserved;
        OrderId = orderId;
        ReservedUntil = until;
    }

    internal void Sell(Guid orderId, DateTimeOffset now)
    {
        if (Status != SeatStatus.Reserved || OrderId != orderId)
            throw new DomainException($"Місце {Code} не зарезервоване для цього замовлення.");
        if (ReservedUntil <= now)
            throw new DomainException($"Термін резервування місця {Code} минув.");
        Status = SeatStatus.Sold;
        ReservedUntil = null;
    }

    internal void Release()
    {
        Status = SeatStatus.Available;
        OrderId = null;
        ReservedUntil = null;
    }
}
