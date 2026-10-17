using Ticketing.Domain.Exceptions;
using Ticketing.Domain.ValueObjects;

namespace Ticketing.Domain.Entities;

public enum SessionStatus { Scheduled, SalesOpen, Cancelled }

/// <summary>
/// Сеанс — корінь агрегату, що володіє місцями. Усі правила резервування й продажу місць зосереджені тут,
/// тому одне місце не може бути закріплене за двома замовленнями.
/// </summary>
public sealed class Session
{
    public const int MaxSeatsPerOrder = 6;
    public static readonly TimeSpan ReservationDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan EntryOpensBeforeStart = TimeSpan.FromHours(2);
    public static readonly TimeSpan EntryClosesAfterStart = TimeSpan.FromHours(3);

    private readonly Dictionary<SeatCode, Seat> _seats;

    public Guid Id { get; } = Guid.NewGuid();
    public Guid EventId { get; }
    public string VenueName { get; }
    public DateTimeOffset StartsAt { get; }
    public SessionStatus Status { get; private set; } = SessionStatus.Scheduled;
    public IReadOnlyCollection<Seat> Seats => _seats.Values;

    private Session(Guid eventId, string venueName, DateTimeOffset startsAt, Dictionary<SeatCode, Seat> seats)
    {
        EventId = eventId;
        VenueName = venueName;
        StartsAt = startsAt;
        _seats = seats;
    }

    public static Session Schedule(
        Guid eventId, string venueName, DateTimeOffset startsAt,
        IEnumerable<(SeatCode Code, Money Price)> layout, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(venueName))
            throw new DomainException("Майданчик сеансу обов'язковий.");
        if (startsAt <= now)
            throw new DomainException("Сеанс має починатися в майбутньому.");

        var seats = new Dictionary<SeatCode, Seat>();
        foreach (var (code, price) in layout)
            if (!seats.TryAdd(code, new Seat(code, price)))
                throw new DomainException($"Місце {code} у схемі залу повторюється.");
        if (seats.Count == 0)
            throw new DomainException("Схема залу має містити хоча б одне місце.");

        return new Session(eventId, venueName.Trim(), startsAt, seats);
    }

    public void OpenSales(DateTimeOffset now)
    {
        if (Status != SessionStatus.Scheduled)
            throw new DomainException("Відкрити продаж можна лише для запланованого сеансу.");
        if (now >= StartsAt)
            throw new DomainException("Сеанс уже розпочався.");
        Status = SessionStatus.SalesOpen;
    }

    public void Cancel()
    {
        if (Status == SessionStatus.Cancelled)
            throw new DomainException("Сеанс уже скасовано.");
        Status = SessionStatus.Cancelled;
    }

    /// <summary>Резервує місця для замовлення. Атомарно: або всі місця, або жодне.</summary>
    public IReadOnlyList<ReservedSeat> ReserveSeats(Guid orderId, IReadOnlyCollection<SeatCode> requested, DateTimeOffset now)
    {
        if (Status != SessionStatus.SalesOpen)
            throw new DomainException("Продаж квитків на сеанс не відкрито.");
        if (now >= StartsAt)
            throw new DomainException("Сеанс уже розпочався.");
        if (requested.Count == 0)
            throw new DomainException("Потрібно вибрати хоча б одне місце.");
        if (requested.Count > MaxSeatsPerOrder)
            throw new DomainException($"В одному замовленні може бути не більше {MaxSeatsPerOrder} місць.");
        if (requested.Distinct().Count() != requested.Count)
            throw new DomainException("Те саме місце вибрано кілька разів.");

        var chosen = new List<Seat>();
        foreach (var code in requested)
        {
            if (!_seats.TryGetValue(code, out var seat))
                throw new DomainException($"Місця {code} не існує на цьому сеансі.");
            if (!seat.IsAvailableAt(now))
                throw new DomainException($"Місце {code} недоступне.");
            chosen.Add(seat);
        }

        var until = now + ReservationDuration;
        chosen.ForEach(s => s.Reserve(orderId, until, now));
        return chosen.Select(s => new ReservedSeat(s.Code, s.Price)).ToList();
    }

    /// <summary>Підтверджує продаж місць замовлення після успішної оплати.</summary>
    public void ConfirmSale(Guid orderId, DateTimeOffset now)
    {
        var reserved = _seats.Values.Where(s => s.Status == SeatStatus.Reserved && s.OrderId == orderId).ToList();
        if (reserved.Count == 0)
            throw new DomainException("Резервування для цього замовлення не знайдено.");
        if (reserved.Any(s => s.ReservedUntil <= now))
            throw new DomainException("Термін резервування місць минув.");
        reserved.ForEach(s => s.Sell(orderId, now));
    }

    /// <summary>Звільняє місця, зарезервовані для замовлення (скасування).</summary>
    public int ReleaseReservation(Guid orderId)
    {
        var reserved = _seats.Values.Where(s => s.Status == SeatStatus.Reserved && s.OrderId == orderId).ToList();
        reserved.ForEach(s => s.Release());
        return reserved.Count;
    }

    /// <summary>Звільняє всі резервування, термін яких минув.</summary>
    public int ReleaseExpiredReservations(DateTimeOffset now)
    {
        var expired = _seats.Values.Where(s => s.Status == SeatStatus.Reserved && s.ReservedUntil <= now).ToList();
        expired.ForEach(s => s.Release());
        return expired.Count;
    }

    /// <summary>Вхід відкритий за 2 год до початку та 3 год після початку, якщо сеанс не скасовано.</summary>
    public bool IsEntryOpen(DateTimeOffset now) =>
        Status != SessionStatus.Cancelled &&
        now >= StartsAt - EntryOpensBeforeStart &&
        now <= StartsAt + EntryClosesAfterStart;
}
