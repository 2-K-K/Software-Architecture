using Ticketing.Application.Ports;
using Ticketing.Application.UseCases;
using Ticketing.Domain.Entities;
using Ticketing.Domain.ValueObjects;
using Ticketing.Infrastructure.Persistence;
using Ticketing.Infrastructure.Services;

namespace Ticketing.Application.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Платіжний шлюз, що рахує виклики (перевіряємо, що кошти не списуються даремно).</summary>
public sealed class SpyPaymentGateway : IPaymentGateway
{
    private readonly SimulatedPaymentGateway _inner = new();
    public int Calls { get; private set; }

    public Task<PaymentResult> ChargeAsync(Guid orderId, Money amount, string paymentToken, CancellationToken ct = default)
    {
        Calls++;
        return _inner.ChargeAsync(orderId, amount, paymentToken, ct);
    }
}

/// <summary>Збирає сценарії поверх репозиторіїв у пам'яті. База даних не потрібна.</summary>
public sealed class TestWorld
{
    public FakeClock Clock { get; } = new();
    public SpyPaymentGateway Gateway { get; } = new();
    public InMemoryOrderRepository Orders { get; } = new();
    public InMemoryTicketRepository Tickets { get; } = new();
    private readonly InMemoryEventRepository _events = new();
    private readonly InMemorySessionRepository _sessions = new();

    public ReserveSeatsHandler Reserve { get; }
    public PayOrderHandler Pay { get; }
    public ValidateTicketHandler Validate { get; }
    public Session Session { get; private set; } = null!;

    public TestWorld(bool publishEvent = true)
    {
        var uow = new InMemoryUnitOfWork();
        var codes = new HmacTicketCodeGenerator(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Reserve = new ReserveSeatsHandler(_events, _sessions, Orders, Clock, uow);
        Pay = new PayOrderHandler(Orders, _sessions, Tickets, Gateway, codes, Clock, uow);
        Validate = new ValidateTicketHandler(Tickets, _sessions, Clock, uow);

        var @event = Event.Create("Концерт", "Філармонія");
        if (publishEvent) @event.Publish();

        var layout = Enumerable.Range(1, 10).Select(n => (new SeatCode(1, n), Money.Uah(500))).ToList();
        Session = Session.Schedule(@event.Id, "Великий зал", Clock.UtcNow.AddDays(19), layout, Clock.UtcNow);
        Session.OpenSales(Clock.UtcNow);

        _events.SaveAsync(@event).GetAwaiter().GetResult();
        _sessions.SaveAsync(Session).GetAwaiter().GetResult();
    }

    public ReserveSeatsCommand ReserveCommand(string email, params (int Row, int Number)[] seats) =>
        new(Session.Id, email, seats.Select(s => new SeatRef(s.Row, s.Number)).ToList());
}
