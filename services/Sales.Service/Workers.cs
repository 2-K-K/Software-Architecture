using System.Threading.Channels;

/// <summary>
/// Асинхронна доставка подій усім підписникам. Для кожного підписника повторює відправку з паузою до 10 с,
/// доки він не прийме подію. Відмова підписника не впливає на продаж: продаж уже завершено до постановки в чергу.
/// Примітка: черга в пам'яті, тому при перезапуску Sales неприйняті події губляться (див. звіт).
/// </summary>
public sealed class TicketEventDispatcher(
    Channel<TicketsIssuedEvent> channel,
    IEnumerable<ITicketsIssuedSubscriber> subscribers,
    ILogger<TicketEventDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            foreach (var subscriber in subscribers)
                await DeliverAsync(subscriber, evt, ct);
    }

    private async Task DeliverAsync(ITicketsIssuedSubscriber subscriber, TicketsIssuedEvent evt, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!await subscriber.HandleAsync(evt, ct))
        {
            log.LogWarning("Підписник {Subscriber} не прийняв подію {EventId}, повтор через {Delay}",
                subscriber.GetType().Name, evt.EventId, delay);
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 2));
        }
    }
}

/// <summary>Повторює підтвердження оплачених замовлень, коли Catalog був недоступний.</summary>
public sealed class PendingConfirmationWorker(SalesStore store, OrderFulfillment fulfillment) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            foreach (var orderId in store.PendingIds())
                await fulfillment.ConfirmAndIssueAsync(orderId, ct);
        }
    }
}
