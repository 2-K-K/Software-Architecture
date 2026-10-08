using Ticketing.Domain.Entities;
using Ticketing.Domain.ValueObjects;

namespace Ticketing.Domain.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset StartsAt = new(2026, 10, 20, 19, 0, 0, TimeSpan.Zero);

    public static SeatCode Seat(int row, int number) => new(row, number);

    /// <summary>Сеанс із продажем: ряд 1 коштує 500 грн, інші ряди — 300 грн.</summary>
    public static Session OpenSession(int rows = 2, int perRow = 5)
    {
        var layout = new List<(SeatCode, Money)>();
        for (var r = 1; r <= rows; r++)
            for (var n = 1; n <= perRow; n++)
                layout.Add((new SeatCode(r, n), Money.Uah(r == 1 ? 500 : 300)));

        var session = Session.Schedule(Guid.NewGuid(), "Великий зал", StartsAt, layout, Now);
        session.OpenSales(Now);
        return session;
    }

    public static IReadOnlyList<ReservedSeat> Reserve(Session session, params SeatCode[] seats) =>
        session.ReserveSeats(Guid.NewGuid(), seats, Now);
}
