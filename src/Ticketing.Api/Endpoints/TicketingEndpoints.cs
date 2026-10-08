using Ticketing.Api.Contracts;
using Ticketing.Application.UseCases;

namespace Ticketing.Api.Endpoints;

/// <summary>
/// Шар подання: приймає HTTP-запити, перевіряє формат вводу, перетворює DTO на команди й передає їх сценаріям.
/// </summary>
public static class TicketingEndpoints
{
    public static IEndpointRouteBuilder MapTicketingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/sessions/{sessionId:guid}/reservations", async (
            Guid sessionId, ReserveSeatsRequest req, ReserveSeatsHandler handler, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.BuyerEmail) || req.Seats is null || req.Seats.Count == 0)
                return Results.BadRequest(new { error = "Потрібно вказати buyerEmail та непорожній список seats." });

            var result = await handler.HandleAsync(new ReserveSeatsCommand(
                sessionId, req.BuyerEmail, req.Seats.Select(s => new SeatRef(s.Row, s.Number)).ToList()), ct);

            return Results.Created($"/api/orders/{result.OrderId}",
                new ReservationResponse(result.OrderId, result.ExpiresAt, result.TotalAmount, result.Currency, result.Seats));
        });

        api.MapPost("/orders/{orderId:guid}/payment", async (
            Guid orderId, PayOrderRequest req, PayOrderHandler handler, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.PaymentToken))
                return Results.BadRequest(new { error = "Потрібно вказати paymentToken." });

            var result = await handler.HandleAsync(new PayOrderCommand(orderId, req.PaymentToken), ct);
            var response = new PaymentResponse(result.Succeeded, result.FailureReason,
                result.Tickets.Select(t => new TicketDto(t.Code, t.Seat)).ToList());
            return result.Succeeded ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status402PaymentRequired);
        });

        api.MapPost("/tickets/validation", async (
            ValidateTicketRequest req, ValidateTicketHandler handler, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest(new { error = "Потрібно вказати code." });

            var result = await handler.HandleAsync(new ValidateTicketCommand(req.Code), ct);
            return Results.Ok(new ValidationResponse(result.Accepted, result.Reason, result.Seat));
        });

        return app;
    }
}
