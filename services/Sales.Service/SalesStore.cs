using Ticketing.Domain.Entities;

/// <summary>
/// Власні дані Sales.Service: замовлення і квитки. Місця й сеанси тут не зберігаються,
/// лише SessionId та ціни на момент резервування (копія потрібних полів).
/// </summary>
public sealed class SalesStore
{
    private readonly Dictionary<Guid, Order> _orders = new();
    private readonly Dictionary<string, Ticket> _ticketsByCode = new();
    private readonly HashSet<Guid> _issued = new();
    private readonly HashSet<Guid> _pending = new();

    public object Gate { get; } = new();

    public void Add(Order order)
    {
        lock (Gate) _orders[order.Id] = order;
    }

    public Order? Find(Guid id)
    {
        lock (Gate) return _orders.GetValueOrDefault(id);
    }

    public Ticket? FindTicket(string code)
    {
        lock (Gate) return _ticketsByCode.GetValueOrDefault(code);
    }

    public void AddTickets(Guid orderId, IEnumerable<Ticket> tickets)
    {
        lock (Gate)
        {
            foreach (var t in tickets) _ticketsByCode[t.Code] = t;
            _issued.Add(orderId);
        }
    }

    public bool IsIssued(Guid orderId)
    {
        lock (Gate) return _issued.Contains(orderId);
    }

    public void SetPending(Guid orderId, bool pending)
    {
        lock (Gate)
        {
            if (pending) _pending.Add(orderId);
            else _pending.Remove(orderId);
        }
    }

    public bool IsPending(Guid orderId)
    {
        lock (Gate) return _pending.Contains(orderId);
    }

    public List<Guid> PendingIds()
    {
        lock (Gate) return _pending.ToList();
    }

    public List<string> CodesFor(Guid orderId)
    {
        lock (Gate)
            return _ticketsByCode.Values.Where(t => t.OrderId == orderId).Select(t => t.Code).ToList();
    }
}
