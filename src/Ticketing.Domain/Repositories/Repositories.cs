using Ticketing.Domain.Entities;

namespace Ticketing.Domain.Repositories;

// Інтерфейси репозиторіїв оголошено в домені в термінах предметної області.
// Реалізації — у шарі Infrastructure (інверсія залежностей).

public interface IEventRepository
{
    Task<Event?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Event @event, CancellationToken ct = default);
}

public interface ISessionRepository
{
    Task<Session?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Session session, CancellationToken ct = default);
}

public interface IOrderRepository
{
    Task<Order?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Order order, CancellationToken ct = default);
}

public interface ITicketRepository
{
    Task AddRangeAsync(IEnumerable<Ticket> tickets, CancellationToken ct = default);
    Task<Ticket?> FindByCodeAsync(string code, CancellationToken ct = default);
    Task<IReadOnlyList<Ticket>> FindByOrderAsync(Guid orderId, CancellationToken ct = default);
}
