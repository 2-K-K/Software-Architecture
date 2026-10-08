namespace Ticketing.Api.Contracts;

// DTO шару подання: окремі від моделей застосунку й домену, щоб зміна формату API не чіпала внутрішні шари.
public sealed record SeatDto(int Row, int Number);
public sealed record ReserveSeatsRequest(string? BuyerEmail, List<SeatDto>? Seats);
public sealed record ReservationResponse(Guid OrderId, DateTimeOffset ExpiresAt, decimal Total, string Currency, IReadOnlyList<string> Seats);
public sealed record PayOrderRequest(string? PaymentToken);
public sealed record TicketDto(string Code, string Seat);
public sealed record PaymentResponse(bool Paid, string? Message, IReadOnlyList<TicketDto> Tickets);
public sealed record ValidateTicketRequest(string? Code);
public sealed record ValidationResponse(bool Accepted, string? Reason, string? Seat);
