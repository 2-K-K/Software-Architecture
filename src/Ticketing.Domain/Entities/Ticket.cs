using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;

namespace Ticketing.Domain.Entities;

public enum TicketStatus { Valid, Used }

/// <summary>Електронний квиток на місце сеансу. Використати його можна лише один раз.</summary>
public sealed class Ticket
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid OrderId { get; }
    public Guid SessionId { get; }
    public SeatCode Seat { get; }
    public string Code { get; }
    public TicketStatus Status { get; private set; } = TicketStatus.Valid;

    private Ticket(Guid orderId, Guid sessionId, SeatCode seat, string code)
    {
        OrderId = orderId;
        SessionId = sessionId;
        Seat = seat;
        Code = code;
    }

    public static Ticket Issue(Guid orderId, Guid sessionId, SeatCode seat, string code) =>
        string.IsNullOrWhiteSpace(code)
            ? throw new DomainException("Код квитка не може бути порожнім.")
            : new Ticket(orderId, sessionId, seat, code.Trim());

    /// <summary>Перевірка на вході: успішна лише для невикористаного квитка.</summary>
    public void Use()
    {
        if (Status == TicketStatus.Used)
            throw new DomainException($"Квиток на місце {Seat} уже використано.");
        Status = TicketStatus.Used;
    }
}
