using System.Threading.Channels;

public enum FulfillKind { Issued, CatalogUnavailable, Rejected }

public sealed record FulfillResult(FulfillKind Kind, string? Error = null);

/// <summary>
/// Підтвердження продажу в Catalog і видача квитків. Використовується і з оплати,
/// і з фонового повтору (PendingConfirmationWorker), тому повторний виклик безпечний.
/// </summary>
public sealed class OrderFulfillment(
    SalesStore store,
    ICatalogSeats seats,
    ITicketFactory ticketFactory,
    Channel<TicketsIssuedEvent> events)
{
    public async Task<FulfillResult> ConfirmAndIssueAsync(Guid orderId, CancellationToken ct)
    {
        if (store.IsIssued(orderId))
            return new FulfillResult(FulfillKind.Issued);

        var order = store.Find(orderId);
        if (order is null)
            return new FulfillResult(FulfillKind.Rejected, "Замовлення не знайдено.");

        try
        {
            await seats.ConfirmAsync(order.SessionId, orderId, ct);
        }
        catch (CatalogRejectedException ex)
        {
            // Наприклад, термін резервування минув. Потребує ручного розгляду.
            store.SetPending(orderId, false);
            return new FulfillResult(FulfillKind.Rejected, ex.Error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Каталог недоступний: оплату прийнято, місця ще не підтверджено. Повторимо пізніше.
            store.SetPending(orderId, true);
            return new FulfillResult(FulfillKind.CatalogUnavailable);
        }

        var tickets = ticketFactory.IssueFor(order);
        store.AddTickets(orderId, tickets);
        store.SetPending(orderId, false);

        // Продуцент лише кладе подію в чергу й не чекає, чи її хтось обробить
        events.Writer.TryWrite(new TicketsIssuedEvent(
            Guid.NewGuid(),
            "TicketsIssued",
            DateTimeOffset.UtcNow,
            new TicketsIssuedPayload(order.Id, order.SessionId, order.BuyerEmail, tickets.Select(t => t.Code).ToList())));

        return new FulfillResult(FulfillKind.Issued);
    }
}
