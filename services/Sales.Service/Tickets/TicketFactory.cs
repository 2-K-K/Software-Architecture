using System.Security.Cryptography;
using Ticketing.Domain.Entities;

/// <summary>
/// Фабрика квитків (Factory Method). Створення квитка і генерація його коду зосереджені тут,
/// тому OrderFulfillment не знає, як саме формується код.
/// </summary>
public interface ITicketFactory
{
    IReadOnlyList<Ticket> IssueFor(Order order);
}

public sealed class RandomTicketFactory : ITicketFactory
{
    public IReadOnlyList<Ticket> IssueFor(Order order) =>
        order.Items
            .Select(i => Ticket.Issue(order.Id, order.SessionId, i.Code, NewCode()))
            .ToList();

    private static string NewCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
}
