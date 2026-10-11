using System.Net.Http.Json;

/// <summary>
/// Підписник (Observer) на подію TicketsIssued. Нові підписники додаються реєстрацією в DI,
/// без зміни продюсера (OrderFulfillment) і диспетчера (TicketEventDispatcher).
/// </summary>
public interface ITicketsIssuedSubscriber
{
    /// <summary>true — подію прийнято; false — повторити пізніше.</summary>
    Task<bool> HandleAsync(TicketsIssuedEvent evt, CancellationToken ct);
}

/// <summary>Доставка події до Notifications.Service через HTTP.</summary>
public sealed class NotificationsSubscriber(IHttpClientFactory httpFactory) : ITicketsIssuedSubscriber
{
    public async Task<bool> HandleAsync(TicketsIssuedEvent evt, CancellationToken ct)
    {
        try
        {
            var response = await httpFactory.CreateClient("notifications").PostAsJsonAsync("/events", evt, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
