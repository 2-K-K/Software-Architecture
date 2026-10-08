using NUnit.Framework;
using Ticketing.Application.Exceptions;
using Ticketing.Application.UseCases;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Exceptions;

namespace Ticketing.Application.Tests;

[TestFixture]
public class ReserveSeatsTests
{
    private TestWorld _w = null!;

    [SetUp]
    public void SetUp() => _w = new TestWorld();

    [Test]
    public async Task Reserve_creates_order_and_holds_seats()
    {
        var result = await _w.Reserve.HandleAsync(_w.ReserveCommand("a@b.ua", (1, 1), (1, 2)));

        Assert.That(result.TotalAmount, Is.EqualTo(1000m));
        Assert.That(result.Currency, Is.EqualTo("UAH"));
        var order = await _w.Orders.FindByIdAsync(result.OrderId);
        Assert.That(order, Is.Not.Null);
        Assert.That(order!.Status, Is.EqualTo(OrderStatus.Created));
        Assert.That(_w.Session.Seats.Count(s => s.Status == SeatStatus.Reserved), Is.EqualTo(2));
    }

    [Test]
    public void Reserve_for_unknown_session_throws_not_found()
    {
        var cmd = new ReserveSeatsCommand(Guid.NewGuid(), "a@b.ua", new[] { new SeatRef(1, 1) });
        Assert.ThrowsAsync<NotFoundException>(async () => await _w.Reserve.HandleAsync(cmd));
    }

    [Test]
    public void Reserve_for_unpublished_event_is_rejected()
    {
        var world = new TestWorld(publishEvent: false);
        Assert.ThrowsAsync<DomainException>(async () =>
            await world.Reserve.HandleAsync(world.ReserveCommand("a@b.ua", (1, 1))));
    }

    [Test]
    public async Task Reserve_with_taken_seat_is_rejected_and_other_seats_stay_free()
    {
        await _w.Reserve.HandleAsync(_w.ReserveCommand("a@b.ua", (1, 1)));

        Assert.ThrowsAsync<DomainException>(async () =>
            await _w.Reserve.HandleAsync(_w.ReserveCommand("c@d.ua", (1, 1), (1, 2))));

        Assert.That(_w.Session.Seats.Single(s => s.Code.Number == 2).Status, Is.EqualTo(SeatStatus.Available));
    }

    [Test]
    public async Task Concurrent_reservations_of_the_same_seat_allow_exactly_one_winner()
    {
        var attempts = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            try
            {
                await _w.Reserve.HandleAsync(_w.ReserveCommand($"buyer{i}@example.com", (1, 1)));
                return true;
            }
            catch (DomainException)
            {
                return false;
            }
        }));

        var results = await Task.WhenAll(attempts);

        Assert.That(results.Count(r => r), Is.EqualTo(1));
    }
}

[TestFixture]
public class PayOrderTests
{
    private TestWorld _w = null!;

    [SetUp]
    public void SetUp() => _w = new TestWorld();

    private async Task<Guid> ReserveAsync(params (int Row, int Number)[] seats) =>
        (await _w.Reserve.HandleAsync(_w.ReserveCommand("a@b.ua", seats))).OrderId;

    [Test]
    public async Task Successful_payment_marks_order_paid_sells_seats_and_issues_tickets()
    {
        var orderId = await ReserveAsync((1, 1), (1, 2));

        var result = await _w.Pay.HandleAsync(new PayOrderCommand(orderId, "tok_ok"));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Tickets.Count, Is.EqualTo(2));
        Assert.That((await _w.Orders.FindByIdAsync(orderId))!.Status, Is.EqualTo(OrderStatus.Paid));
        Assert.That(_w.Session.Seats.Count(s => s.Status == SeatStatus.Sold), Is.EqualTo(2));
        Assert.That((await _w.Tickets.FindByOrderAsync(orderId)).Count, Is.EqualTo(2));
    }

    [Test]
    public async Task Declined_payment_issues_no_tickets_and_keeps_reservation()
    {
        var orderId = await ReserveAsync((1, 1));

        var result = await _w.Pay.HandleAsync(new PayOrderCommand(orderId, "declined"));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Tickets.Count, Is.EqualTo(0));
        Assert.That((await _w.Orders.FindByIdAsync(orderId))!.Status, Is.EqualTo(OrderStatus.Created));
        Assert.That(_w.Session.Seats.Single(s => s.Code.Number == 1).Status, Is.EqualTo(SeatStatus.Reserved));
    }

    [Test]
    public async Task Payment_after_reservation_expired_is_rejected_before_charging()
    {
        var orderId = await ReserveAsync((1, 1));
        _w.Clock.Advance(Session.ReservationDuration);

        Assert.ThrowsAsync<DomainException>(async () =>
            await _w.Pay.HandleAsync(new PayOrderCommand(orderId, "tok_ok")));
        Assert.That(_w.Gateway.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task Paying_twice_is_rejected_and_does_not_charge_again()
    {
        var orderId = await ReserveAsync((1, 1));
        await _w.Pay.HandleAsync(new PayOrderCommand(orderId, "tok_ok"));

        Assert.ThrowsAsync<DomainException>(async () =>
            await _w.Pay.HandleAsync(new PayOrderCommand(orderId, "tok_ok")));
        Assert.That(_w.Gateway.Calls, Is.EqualTo(1));
    }

    [Test]
    public void Paying_unknown_order_throws_not_found() =>
        Assert.ThrowsAsync<NotFoundException>(async () =>
            await _w.Pay.HandleAsync(new PayOrderCommand(Guid.NewGuid(), "tok_ok")));
}

[TestFixture]
public class ValidateTicketTests
{
    private TestWorld _w = null!;

    [SetUp]
    public void SetUp() => _w = new TestWorld();

    private async Task<string> IssueTicketAsync()
    {
        var order = await _w.Reserve.HandleAsync(_w.ReserveCommand("a@b.ua", (1, 1)));
        var paid = await _w.Pay.HandleAsync(new PayOrderCommand(order.OrderId, "tok_ok"));
        return paid.Tickets.Single().Code;
    }

    [Test]
    public async Task Valid_ticket_is_accepted_once_during_entry_window()
    {
        var code = await IssueTicketAsync();
        _w.Clock.UtcNow = _w.Session.StartsAt.AddMinutes(-30);

        var first = await _w.Validate.HandleAsync(new ValidateTicketCommand(code));
        var second = await _w.Validate.HandleAsync(new ValidateTicketCommand(code));

        Assert.That(first.Accepted, Is.True);
        Assert.That(second.Accepted, Is.False);
    }

    [Test]
    public async Task Ticket_is_rejected_before_entry_opens()
    {
        var code = await IssueTicketAsync();
        var result = await _w.Validate.HandleAsync(new ValidateTicketCommand(code));
        Assert.That(result.Accepted, Is.False);
    }

    [Test]
    public async Task Forged_code_is_rejected()
    {
        var result = await _w.Validate.HandleAsync(new ValidateTicketCommand("forged-code"));
        Assert.That(result.Accepted, Is.False);
        Assert.That(result.Reason, Is.EqualTo("Квиток не знайдено."));
    }

    [Test]
    public async Task Empty_code_is_rejected()
    {
        var result = await _w.Validate.HandleAsync(new ValidateTicketCommand(""));
        Assert.That(result.Accepted, Is.False);
    }
}
