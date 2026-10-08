using Ticketing.Application.Ports;
using Ticketing.Domain.Exceptions;
using Ticketing.Domain.Repositories;

namespace Ticketing.Application.UseCases;

public sealed record ValidateTicketCommand(string Code);
public sealed record ValidateTicketResult(bool Accepted, string? Reason, string? Seat);

/// <summary>Сценарій 3. Перевірка квитка контролером на вході.</summary>
public sealed class ValidateTicketHandler
{
    private readonly ITicketRepository _tickets;
    private readonly ISessionRepository _sessions;
    private readonly IClock _clock;
    private readonly IUnitOfWork _uow;

    public ValidateTicketHandler(ITicketRepository tickets, ISessionRepository sessions, IClock clock, IUnitOfWork uow)
    {
        _tickets = tickets;
        _sessions = sessions;
        _clock = clock;
        _uow = uow;
    }

    public Task<ValidateTicketResult> HandleAsync(ValidateTicketCommand cmd, CancellationToken ct = default) =>
        _uow.ExecuteAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(cmd.Code))
                return new ValidateTicketResult(false, "Код квитка не вказано.", null);

            var ticket = await _tickets.FindByCodeAsync(cmd.Code.Trim(), ct);
            if (ticket is null)
                return new ValidateTicketResult(false, "Квиток не знайдено.", null);

            var session = await _sessions.FindByIdAsync(ticket.SessionId, ct);
            if (session is null || !session.IsEntryOpen(_clock.UtcNow))
                return new ValidateTicketResult(false, "Вхід на сеанс зараз закрито.", ticket.Seat.ToString());

            try
            {
                ticket.Use();
            }
            catch (DomainException ex)
            {
                return new ValidateTicketResult(false, ex.Message, ticket.Seat.ToString());
            }
            return new ValidateTicketResult(true, null, ticket.Seat.ToString());
        }, ct);
}
