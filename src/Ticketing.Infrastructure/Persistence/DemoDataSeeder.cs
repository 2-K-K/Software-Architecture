using Ticketing.Application.Ports;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Repositories;
using Ticketing.Domain.ValueObjects;

namespace Ticketing.Infrastructure.Persistence;

public sealed record DemoData(Guid EventId, Guid SessionId);

/// <summary>Наповнює сховище демонстраційними даними: один захід і один сеанс із відкритим продажем.</summary>
public sealed class DemoDataSeeder
{
    private readonly IEventRepository _events;
    private readonly ISessionRepository _sessions;
    private readonly IClock _clock;

    public DemoDataSeeder(IEventRepository events, ISessionRepository sessions, IClock clock)
    {
        _events = events;
        _sessions = sessions;
        _clock = clock;
    }

    public async Task<DemoData> SeedAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var @event = Event.Create("Концерт симфонічного оркестру", "Одеська філармонія");
        @event.Publish();

        var layout = new List<(SeatCode, Money)>();
        for (var row = 1; row <= 5; row++)
            for (var number = 1; number <= 10; number++)
                layout.Add((new SeatCode(row, number), Money.Uah(row <= 3 ? 500 : 300)));

        var session = Session.Schedule(@event.Id, "Великий зал", now.AddDays(30), layout, now);
        session.OpenSales(now);

        await _events.SaveAsync(@event, ct);
        await _sessions.SaveAsync(session, ct);
        return new DemoData(@event.Id, session.Id);
    }
}
