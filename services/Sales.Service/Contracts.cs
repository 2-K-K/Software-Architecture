// Контракти Sales.Service. Відповідають services/contracts/*.yaml і events/tickets-issued.v1.json.

public sealed record PlaceOrderRequest(Guid SessionId, string BuyerEmail, List<SeatRef> Seats);
public sealed record ValidateTicketRequest(string? Code);
public sealed record SeatRef(int Row, int Number);

// Вихідні запити до Catalog.Service
public sealed record ReserveRequest(Guid OrderId, List<SeatRef> Seats);
public sealed record ConfirmRequest(Guid OrderId);

// Подія, яку Sales публікує і яку споживає Notifications.Service
public sealed record TicketsIssuedEvent(Guid EventId, string Type, DateTimeOffset OccurredAt, TicketsIssuedPayload Payload);
public sealed record TicketsIssuedPayload(Guid OrderId, Guid SessionId, string BuyerEmail, List<string> TicketCodes);
