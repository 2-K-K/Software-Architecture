using Ticketing.Domain.Exceptions;

namespace Ticketing.Domain.Entities;

public enum EventStatus { Draft, Published, Cancelled }

/// <summary>Захід: культурна подія, на яку організатор проводить сеанси.</summary>
public sealed class Event
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public string OrganizerName { get; }
    public EventStatus Status { get; private set; } = EventStatus.Draft;

    private Event(string title, string organizerName)
    {
        Title = title;
        OrganizerName = organizerName;
    }

    public static Event Create(string title, string organizerName)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200)
            throw new DomainException("Назва заходу обов'язкова й має містити не більше 200 символів.");
        if (string.IsNullOrWhiteSpace(organizerName))
            throw new DomainException("Організатор заходу обов'язковий.");
        return new Event(title.Trim(), organizerName.Trim());
    }

    public void Publish()
    {
        if (Status != EventStatus.Draft)
            throw new DomainException("Опублікувати можна лише захід у стані «чернетка».");
        Status = EventStatus.Published;
    }

    public void Cancel()
    {
        if (Status == EventStatus.Cancelled)
            throw new DomainException("Захід уже скасовано.");
        Status = EventStatus.Cancelled;
    }

    /// <summary>Продавати квитки можна лише на опублікований і не скасований захід.</summary>
    public void EnsureOnSale()
    {
        if (Status != EventStatus.Published)
            throw new DomainException("Захід не опубліковано або скасовано: продаж неможливий.");
    }
}
