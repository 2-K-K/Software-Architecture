using NUnit.Framework;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;
using static Ticketing.Domain.Tests.TestData;

namespace Ticketing.Domain.Tests;

[TestFixture]
public class TicketTests
{
    private static Ticket NewTicket() => Ticket.Issue(Guid.NewGuid(), Guid.NewGuid(), Seat(1, 1), "CODE-1");

    [Test]
    public void New_ticket_is_valid() =>
        Assert.That(NewTicket().Status, Is.EqualTo(TicketStatus.Valid));

    [Test]
    public void Use_marks_ticket_as_used()
    {
        var ticket = NewTicket();
        ticket.Use();
        Assert.That(ticket.Status, Is.EqualTo(TicketStatus.Used));
    }

    [Test]
    public void Use_twice_is_rejected()
    {
        var ticket = NewTicket();
        ticket.Use();
        Assert.Throws<DomainException>(() => ticket.Use());
    }

    [Test]
    public void Empty_code_is_rejected() =>
        Assert.Throws<DomainException>(() => Ticket.Issue(Guid.NewGuid(), Guid.NewGuid(), Seat(1, 1), "  "));
}

[TestFixture]
public class EventTests
{
    [TestCase("")]
    [TestCase("   ")]
    public void Create_requires_title(string title) =>
        Assert.Throws<DomainException>(() => Event.Create(title, "Організатор"));

    [Test]
    public void Create_requires_organizer() =>
        Assert.Throws<DomainException>(() => Event.Create("Концерт", ""));

    [Test]
    public void New_event_is_draft_and_cannot_be_sold()
    {
        var e = Event.Create("Концерт", "Організатор");
        Assert.That(e.Status, Is.EqualTo(EventStatus.Draft));
        Assert.Throws<DomainException>(() => e.EnsureOnSale());
    }

    [Test]
    public void Published_event_can_be_sold()
    {
        var e = Event.Create("Концерт", "Організатор");
        e.Publish();
        Assert.DoesNotThrow(() => e.EnsureOnSale());
    }

    [Test]
    public void Publish_twice_is_rejected()
    {
        var e = Event.Create("Концерт", "Організатор");
        e.Publish();
        Assert.Throws<DomainException>(() => e.Publish());
    }

    [Test]
    public void Cancelled_event_cannot_be_sold_or_cancelled_again()
    {
        var e = Event.Create("Концерт", "Організатор");
        e.Publish();
        e.Cancel();
        Assert.Throws<DomainException>(() => e.EnsureOnSale());
        Assert.Throws<DomainException>(() => e.Cancel());
    }
}

[TestFixture]
public class ValueObjectTests
{
    [TestCase(0, 1)]
    [TestCase(1, 0)]
    [TestCase(-1, 5)]
    public void SeatCode_requires_positive_row_and_number(int row, int number) =>
        Assert.Throws<DomainException>(() => new SeatCode(row, number));

    [Test]
    public void SeatCode_has_value_equality() =>
        Assert.That(new SeatCode(2, 7), Is.EqualTo(new SeatCode(2, 7)));

    [Test]
    public void Money_rejects_negative_amount() =>
        Assert.Throws<DomainException>(() => Money.Uah(-1));

    [Test]
    public void Money_adds_amounts() =>
        Assert.That((Money.Uah(500) + Money.Uah(300)).Amount, Is.EqualTo(800m));
}
