using Ticketing.Application.Ports;

namespace Ticketing.Infrastructure.Persistence;

/// <summary>
/// Емуляція серіалізованої транзакції для сховища в пам'яті: одночасно виконується один сценарій.
/// У ЛР5 замінюється транзакцією бази даних з обмеженнями унікальності.
/// </summary>
public sealed class InMemoryUnitOfWork : IUnitOfWork
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await work(); }
        finally { _gate.Release(); }
    }
}
