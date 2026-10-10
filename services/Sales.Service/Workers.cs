using System.Net.Http.Json;
using System.Threading.Channels;

/// <summary>
/// Асинхронна доставка подій споживачу Notifications.Service.
/// Повторює відправку з паузою до 10 с, доки споживач не прийме подію.
/// Відмова споживача не впливає на продаж: продаж уже завершено до постановки в чергу.
/// Примітка: черга в пам'яті, тому при перезапуску Sales неприйняті події губляться (див. звіт).
/// </summary>
public sealed class TicketEventDispatcher(
    Channel<TicketsIssuedEvent> channel,
    IHttpClientFactory httpFactory,
    ILogger<TicketEventDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var client = httpFactory.CreateClient("notifications");

        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
        {
            var delay = TimeSpan.FromSeconds(1);
            while (true)
            {
                try
                {
                    var response = await client.PostAsJsonAsync("/events", evt, ct);
                    if (response.IsSuccessStatusCode) break;
                    log.LogWarning("Споживач відповів {Code}, повтор події {EventId}", (int)response.StatusCode, evt.EventId);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    log.LogWarning("Споживач недоступний, повтор події {EventId} через {Delay}", evt.EventId, delay);
                }

                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 2));
            }
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
