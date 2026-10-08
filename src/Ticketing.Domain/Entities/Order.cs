using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;

namespace Ticketing.Domain.Entities;

public enum OrderStatus { Created, Paid, Cancelled }

/// <summary>Замовлення покупця. Стани: Created -> Paid | Cancelled.</summary>
public sealed class Order
{
    public Guid Id { get; }
    public Guid SessionId { get; }
    public string BuyerEmail { get; }
    public IReadOnlyList<ReservedSeat> Items { get; }
    public DateTimeOffset ExpiresAt { get; }
    public OrderStatus Status { get; private set; } = OrderStatus.Created;
    public string? PaymentReference { get; private set; }

    public Money Total => Items.Aggregate(Money.Zero, (sum, i) => sum + i.Price);

    private Order(Guid id, Guid sessionId, string email, IReadOnlyList<ReservedSeat> items, DateTimeOffset expiresAt)
    {
        Id = id;
        SessionId = sessionId;
        BuyerEmail = email;
        Items = items;
        ExpiresAt = expiresAt;
    }

    public static Order Place(Guid orderId, Guid sessionId, string buyerEmail,
        IReadOnlyList<ReservedSeat> seats, DateTimeOffset reservedUntil)
    {
        if (string.IsNullOrWhiteSpace(buyerEmail) || !buyerEmail.Contains('@'))
            throw new DomainException("Вкажіть коректну адресу електронної пошти покупця.");
        if (seats.Count < 1 || seats.Count > Session.MaxSeatsPerOrder)
            throw new DomainException($"Замовлення має містити від 1 до {Session.MaxSeatsPerOrder} місць.");
        return new Order(orderId, sessionId, buyerEmail.Trim(), seats, reservedUntil);
    }

    /// <summary>Замовлення можна оплатити, лише поки воно «Created» і не минув термін резервування.</summary>
    public void EnsurePayable(DateTimeOffset now)
    {
        if (Status != OrderStatus.Created)
            throw new DomainException($"Замовлення в стані «{Status}» не можна оплатити.");
        if (now >= ExpiresAt)
            throw new DomainException("Термін оплати замовлення минув.");
    }

    public void MarkPaid(string paymentReference, DateTimeOffset now)
    {
        EnsurePayable(now);
        if (string.IsNullOrWhiteSpace(paymentReference))
            throw new DomainException("Для оплаченого замовлення потрібен ідентифікатор платежу.");
        Status = OrderStatus.Paid;
        PaymentReference = paymentReference;
    }

    public void Cancel()
    {
        if (Status != OrderStatus.Created)
            throw new DomainException("Скасувати можна лише неоплачене замовлення.");
        Status = OrderStatus.Cancelled;
    }
}
