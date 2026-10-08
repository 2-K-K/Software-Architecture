using Ticketing.Application.Exceptions;
using Ticketing.Application.Ports;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Repositories;
using Ticketing.Domain.ValueObjects;

namespace Ticketing.Application.UseCases;

public sealed record SeatRef(int Row, int Number);
public sealed record ReserveSeatsCommand(Guid SessionId, string BuyerEmail, IReadOnlyList<SeatRef> Seats);
public sealed record ReserveSeatsResult(Guid OrderId, DateTimeOffset ExpiresAt, decimal TotalAmount, string Currency, IReadOnlyList<string> Seats);

/// <summary>
/// Сценарій 1. Резервування місць і створення замовлення.
/// Правила виконує домен; тут лише оркестрація та транзакційна межа.
/// </summary>
public sealed class ReserveSeatsHandler
{
    private readonly IEventRepository _events;
    private readonly ISessionRepository _sessions;
    private readonly IOrderRepository _orders;
    private readonly IClock _clock;
    private readonly IUnitOfWork _uow;

    public ReserveSeatsHandler(IEventRepository events, ISessionRepository sessions, IOrderRepository orders,
        IClock clock, IUnitOfWork uow)
    {
        _events = events;
        _sessions = sessions;
        _orders = orders;
        _clock = clock;
        _uow = uow;
    }

    public Task<ReserveSeatsResult> HandleAsync(ReserveSeatsCommand cmd, CancellationToken ct = default) =>
        _uow.ExecuteAsync(async () =>
        {
            var now = _clock.UtcNow;
            var session = await _sessions.FindByIdAsync(cmd.SessionId, ct)
                ?? throw new NotFoundException(nameof(Session), cmd.SessionId);
            var @event = await _events.FindByIdAsync(session.EventId, ct)
                ?? throw new NotFoundException(nameof(Event), session.EventId);
            @event.EnsureOnSale();

            var orderId = Guid.NewGuid();
            var seats = cmd.Seats.Select(s => new SeatCode(s.Row, s.Number)).ToList();
            var reserved = session.ReserveSeats(orderId, seats, now);
            var order = Order.Place(orderId, session.Id, cmd.BuyerEmail, reserved, now + Session.ReservationDuration);

            await _sessions.SaveAsync(session, ct);
            await _orders.SaveAsync(order, ct);

            return new ReserveSeatsResult(order.Id, order.ExpiresAt, order.Total.Amount, order.Total.Currency,
                order.Items.Select(i => i.Code.ToString()).ToList());
        }, ct);
}
