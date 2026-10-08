using Ticketing.Application.Exceptions;
using Ticketing.Application.Ports;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Repositories;

namespace Ticketing.Application.UseCases;

public sealed record PayOrderCommand(Guid OrderId, string PaymentToken);
public sealed record IssuedTicket(string Code, string Seat);
public sealed record PayOrderResult(bool Succeeded, string? FailureReason, IReadOnlyList<IssuedTicket> Tickets);

/// <summary>
/// Сценарій 2. Оплата замовлення та видача квитків.
/// Виклик платіжного шлюзу виконується поза транзакцією; фіксація результату — в окремій транзакційній межі.
/// </summary>
public sealed class PayOrderHandler
{
    private readonly IOrderRepository _orders;
    private readonly ISessionRepository _sessions;
    private readonly ITicketRepository _tickets;
    private readonly IPaymentGateway _gateway;
    private readonly ITicketCodeGenerator _codes;
    private readonly IClock _clock;
    private readonly IUnitOfWork _uow;

    public PayOrderHandler(IOrderRepository orders, ISessionRepository sessions, ITicketRepository tickets,
        IPaymentGateway gateway, ITicketCodeGenerator codes, IClock clock, IUnitOfWork uow)
    {
        _orders = orders;
        _sessions = sessions;
        _tickets = tickets;
        _gateway = gateway;
        _codes = codes;
        _clock = clock;
        _uow = uow;
    }

    public async Task<PayOrderResult> HandleAsync(PayOrderCommand cmd, CancellationToken ct = default)
    {
        var order = await _orders.FindByIdAsync(cmd.OrderId, ct)
            ?? throw new NotFoundException(nameof(Order), cmd.OrderId);

        // Не списуємо кошти за замовлення, яке вже не можна оплатити.
        order.EnsurePayable(_clock.UtcNow);

        var payment = await _gateway.ChargeAsync(order.Id, order.Total, cmd.PaymentToken, ct);
        if (!payment.Succeeded)
            return new PayOrderResult(false, payment.FailureReason ?? "Платіж відхилено.", Array.Empty<IssuedTicket>());

        return await _uow.ExecuteAsync(async () =>
        {
            var now = _clock.UtcNow;
            var session = await _sessions.FindByIdAsync(order.SessionId, ct)
                ?? throw new NotFoundException(nameof(Session), order.SessionId);

            session.ConfirmSale(order.Id, now);
            order.MarkPaid(payment.Reference!, now);

            var tickets = order.Items
                .Select(i => Ticket.Issue(order.Id, session.Id, i.Code, _codes.Generate(session.Id, i.Code)))
                .ToList();

            await _sessions.SaveAsync(session, ct);
            await _orders.SaveAsync(order, ct);
            await _tickets.AddRangeAsync(tickets, ct);

            return new PayOrderResult(true, null, tickets.Select(t => new IssuedTicket(t.Code, t.Seat.ToString())).ToList());
        }, ct);
    }
}
