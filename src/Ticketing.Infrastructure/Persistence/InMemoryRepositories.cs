using System.Collections.Concurrent;
using Ticketing.Domain.Entities;
using Ticketing.Domain.Repositories;

namespace Ticketing.Infrastructure.Persistence;

// Реалізації репозиторіїв у пам'яті. Повністю відповідають інтерфейсам домену;
// у ЛР5 їх буде замінено реалізаціями для реальної СУБД без змін у Domain і Application.

public sealed class InMemoryEventRepository : IEventRepository
{
    private readonly ConcurrentDictionary<Guid, Event> _store = new();
    public Task<Event?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault(id));
    public Task SaveAsync(Event @event, CancellationToken ct = default)
    {
        _store[@event.Id] = @event;
        return Task.CompletedTask;
    }
}

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly ConcurrentDictionary<Guid, Session> _store = new();
    public Task<Session?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault(id));
    public Task SaveAsync(Session session, CancellationToken ct = default)
    {
        _store[session.Id] = session;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> _store = new();
    public Task<Order?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault(id));
    public Task SaveAsync(Order order, CancellationToken ct = default)
    {
        _store[order.Id] = order;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryTicketRepository : ITicketRepository
{
    private readonly ConcurrentDictionary<string, Ticket> _byCode = new();
    public Task AddRangeAsync(IEnumerable<Ticket> tickets, CancellationToken ct = default)
    {
        foreach (var t in tickets) _byCode[t.Code] = t;
        return Task.CompletedTask;
    }
    public Task<Ticket?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(_byCode.GetValueOrDefault(code));
    public Task<IReadOnlyList<Ticket>> FindByOrderAsync(Guid orderId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Ticket>>(_byCode.Values.Where(t => t.OrderId == orderId).ToList());
}
