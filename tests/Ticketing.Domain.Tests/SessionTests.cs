using NUnit.Framework;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;
using static Ticketing.Domain.Tests.TestData;

namespace Ticketing.Domain.Tests;

[TestFixture]
public class SessionTests
{
    private static readonly (SeatCode, Money)[] OneSeat = { (new SeatCode(1, 1), Money.Uah(100)) };

    [Test]
    public void Schedule_in_the_past_is_rejected() =>
        Assert.Throws<DomainException>(() => Session.Schedule(Guid.NewGuid(), "Зал", Now.AddMinutes(-1), OneSeat, Now));

    [Test]
    public void Schedule_without_venue_is_rejected() =>
        Assert.Throws<DomainException>(() => Session.Schedule(Guid.NewGuid(), " ", StartsAt, OneSeat, Now));

    [Test]
    public void Schedule_with_duplicate_seats_is_rejected()
    {
        var layout = new[] { (Seat(1, 1), Money.Uah(100)), (Seat(1, 1), Money.Uah(100)) };
        Assert.Throws<DomainException>(() => Session.Schedule(Guid.NewGuid(), "Зал", StartsAt, layout, Now));
    }

    [Test]
    public void Schedule_without_seats_is_rejected() =>
        Assert.Throws<DomainException>(() =>
            Session.Schedule(Guid.NewGuid(), "Зал", StartsAt, Array.Empty<(SeatCode, Money)>(), Now));

    [Test]
    public void Reserve_requires_open_sales()
    {
        var session = Session.Schedule(Guid.NewGuid(), "Зал", StartsAt, OneSeat, Now);
        Assert.Throws<DomainException>(() => Reserve(session, Seat(1, 1)));
    }

    [Test]
    public void Reserve_marks_seats_reserved_and_returns_prices()
    {
        var session = OpenSession();
        var reserved = Reserve(session, Seat(1, 1), Seat(2, 1));

        Assert.That(reserved.Count, Is.EqualTo(2));
        Assert.That(reserved.Sum(s => s.Price.Amount), Is.EqualTo(800m));
        Assert.That(session.Seats.Count(s => s.Status == SeatStatus.Reserved), Is.EqualTo(2));
    }

    [Test]
    public void Reserve_more_than_limit_is_rejected()
    {
        var session = OpenSession();
        var seven = session.Seats.Select(s => s.Code).Take(Session.MaxSeatsPerOrder + 1).ToArray();
        Assert.Throws<DomainException>(() => Reserve(session, seven));
    }

    [Test]
    public void Reserve_exactly_the_limit_is_allowed()
    {
        var session = OpenSession();
        var six = session.Seats.Select(s => s.Code).Take(Session.MaxSeatsPerOrder).ToArray();
        Assert.DoesNotThrow(() => Reserve(session, six));
    }

    [Test]
    public void Reserve_nothing_is_rejected()
    {
        var session = OpenSession();
        Assert.Throws<DomainException>(() => Reserve(session));
    }

    [Test]
    public void Reserve_same_seat_twice_in_one_request_is_rejected()
    {
        var session = OpenSession();
        Assert.Throws<DomainException>(() => Reserve(session, Seat(1, 1), Seat(1, 1)));
    }

    [Test]
    public void Reserve_unknown_seat_is_rejected()
    {
        var session = OpenSession();
        Assert.Throws<DomainException>(() => Reserve(session, Seat(9, 9)));
    }

    [Test]
    public void Reserve_seat_held_by_another_order_is_rejected()
    {
        var session = OpenSession();
        Reserve(session, Seat(1, 1));
        Assert.Throws<DomainException>(() => Reserve(session, Seat(1, 1)));
    }

    [Test]
    public void Reserve_is_atomic_when_one_of_the_seats_is_unavailable()
    {
        var session = OpenSession();
        Reserve(session, Seat(1, 2));

        Assert.Throws<DomainException>(() => Reserve(session, Seat(1, 1), Seat(1, 2)));

        Assert.That(session.Seats.Single(s => s.Code == Seat(1, 1)).Status, Is.EqualTo(SeatStatus.Available));
    }

    [Test]
    public void Reserve_after_session_start_is_rejected()
    {
        var session = OpenSession();
        Assert.Throws<DomainException>(() => session.ReserveSeats(Guid.NewGuid(), new[] { Seat(1, 1) }, StartsAt));
    }

    [Test]
    public void Seat_with_expired_reservation_can_be_reserved_by_another_order()
    {
        var session = OpenSession();
        Reserve(session, Seat(1, 1));
        var later = Now + Session.ReservationDuration;

        var second = session.ReserveSeats(Guid.NewGuid(), new[] { Seat(1, 1) }, later);

        Assert.That(second.Count, Is.EqualTo(1));
    }

    [Test]
    public void ConfirmSale_turns_reserved_seats_into_sold()
    {
        var session = OpenSession();
        var orderId = Guid.NewGuid();
        session.ReserveSeats(orderId, new[] { Seat(1, 1), Seat(1, 2) }, Now);

        session.ConfirmSale(orderId, Now.AddMinutes(5));

        Assert.That(session.Seats.Count(s => s.Status == SeatStatus.Sold), Is.EqualTo(2));
    }

    [Test]
    public void ConfirmSale_after_reservation_expired_is_rejected()
    {
        var session = OpenSession();
        var orderId = Guid.NewGuid();
        session.ReserveSeats(orderId, new[] { Seat(1, 1) }, Now);

        Assert.Throws<DomainException>(() => session.ConfirmSale(orderId, Now + Session.ReservationDuration));
    }

    [Test]
    public void ConfirmSale_for_unknown_order_is_rejected()
    {
        var session = OpenSession();
        Assert.Throws<DomainException>(() => session.ConfirmSale(Guid.NewGuid(), Now));
    }

    [Test]
    public void Sold_seat_cannot_be_reserved_again()
    {
        var session = OpenSession();
        var orderId = Guid.NewGuid();
        session.ReserveSeats(orderId, new[] { Seat(1, 1) }, Now);
        session.ConfirmSale(orderId, Now.AddMinutes(1));

        Assert.Throws<DomainException>(() =>
            session.ReserveSeats(Guid.NewGuid(), new[] { Seat(1, 1) }, Now.AddDays(1)));
    }

    [Test]
    public void ReleaseReservation_frees_only_seats_of_that_order()
    {
        var session = OpenSession();
        var orderId = Guid.NewGuid();
        session.ReserveSeats(orderId, new[] { Seat(1, 1), Seat(1, 2) }, Now);
        Reserve(session, Seat(1, 3));

        var released = session.ReleaseReservation(orderId);

        Assert.That(released, Is.EqualTo(2));
        Assert.That(session.Seats.Count(s => s.Status == SeatStatus.Reserved), Is.EqualTo(1));
    }

    [Test]
    public void ReleaseExpiredReservations_frees_only_expired_ones()
    {
        var session = OpenSession();
        Reserve(session, Seat(1, 1));
        session.ReserveSeats(Guid.NewGuid(), new[] { Seat(1, 2) }, Now.AddMinutes(10));

        var released = session.ReleaseExpiredReservations(Now.AddMinutes(16));

        Assert.That(released, Is.EqualTo(1));
        Assert.That(session.Seats.Single(s => s.Code == Seat(1, 2)).Status, Is.EqualTo(SeatStatus.Reserved));
    }

    [TestCase(-121, false)]
    [TestCase(-119, true)]
    [TestCase(0, true)]
    [TestCase(179, true)]
    [TestCase(181, false)]
    public void Entry_window_is_two_hours_before_and_three_hours_after_start(int minutesFromStart, bool expected)
    {
        var session = OpenSession();
        Assert.That(session.IsEntryOpen(StartsAt.AddMinutes(minutesFromStart)), Is.EqualTo(expected));
    }

    [Test]
    public void Entry_is_closed_for_cancelled_session()
    {
        var session = OpenSession();
        session.Cancel();
        Assert.That(session.IsEntryOpen(StartsAt), Is.False);
    }

    [Test]
    public void Cancel_twice_is_rejected()
    {
        var session = OpenSession();
        session.Cancel();
        Assert.Throws<DomainException>(() => session.Cancel());
    }
}
