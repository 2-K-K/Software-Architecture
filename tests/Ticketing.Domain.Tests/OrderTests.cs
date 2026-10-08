using NUnit.Framework;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;
using static Ticketing.Domain.Tests.TestData;

namespace Ticketing.Domain.Tests;

[TestFixture]
public class OrderTests
{
    private Session _session = null!;

    [SetUp]
    public void SetUp() => _session = OpenSession();

    private Order PlaceOrder(params SeatCode[] seats)
    {
        var orderId = Guid.NewGuid();
        var reserved = _session.ReserveSeats(orderId, seats, Now);
        return Order.Place(orderId, _session.Id, "buyer@example.com", reserved, Now + Session.ReservationDuration);
    }

    [Test]
    public void Place_computes_total_from_items()
    {
        var order = PlaceOrder(Seat(1, 1), Seat(2, 1));
        Assert.That(order.Total.Amount, Is.EqualTo(800m));
        Assert.That(order.Status, Is.EqualTo(OrderStatus.Created));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not-an-email")]
    public void Place_requires_valid_email(string email)
    {
        var reserved = Reserve(_session, Seat(1, 1));
        Assert.Throws<DomainException>(() =>
            Order.Place(Guid.NewGuid(), _session.Id, email, reserved, Now.AddMinutes(15)));
    }

    [Test]
    public void Place_without_seats_is_rejected() =>
        Assert.Throws<DomainException>(() =>
            Order.Place(Guid.NewGuid(), _session.Id, "a@b.ua", Array.Empty<ReservedSeat>(), Now.AddMinutes(15)));

    [Test]
    public void MarkPaid_before_expiry_changes_status()
    {
        var order = PlaceOrder(Seat(1, 1));
        order.MarkPaid("PAY-1", Now.AddMinutes(5));
        Assert.That(order.Status, Is.EqualTo(OrderStatus.Paid));
        Assert.That(order.PaymentReference, Is.EqualTo("PAY-1"));
    }

    [Test]
    public void MarkPaid_at_or_after_expiry_is_rejected()
    {
        var order = PlaceOrder(Seat(1, 1));
        Assert.Throws<DomainException>(() => order.MarkPaid("PAY-1", order.ExpiresAt));
    }

    [Test]
    public void MarkPaid_twice_is_rejected()
    {
        var order = PlaceOrder(Seat(1, 1));
        order.MarkPaid("PAY-1", Now.AddMinutes(1));
        Assert.Throws<DomainException>(() => order.MarkPaid("PAY-2", Now.AddMinutes(2)));
    }

    [Test]
    public void MarkPaid_requires_payment_reference()
    {
        var order = PlaceOrder(Seat(1, 1));
        Assert.Throws<DomainException>(() => order.MarkPaid("", Now.AddMinutes(1)));
    }

    [Test]
    public void Cancel_is_allowed_only_for_unpaid_order()
    {
        var order = PlaceOrder(Seat(1, 1));
        order.MarkPaid("PAY-1", Now.AddMinutes(1));
        Assert.Throws<DomainException>(() => order.Cancel());
    }

    [Test]
    public void Cancelled_order_cannot_be_paid()
    {
        var order = PlaceOrder(Seat(1, 1));
        order.Cancel();
        Assert.Throws<DomainException>(() => order.EnsurePayable(Now));
    }
}
