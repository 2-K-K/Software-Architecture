using Ticketing.Domain.ValueObjects;

namespace Ticketing.Application.Ports;

// Порти: абстракції, які потрібні сценаріям, але реалізуються в Infrastructure.

/// <summary>Джерело часу; дозволяє тестувати правила, що залежать від часу.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Транзакційна межа сценарію: усі зміни всередині <c>work</c> застосовуються разом або не застосовуються взагалі.
/// Конкретний механізм визначає Infrastructure
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<Task<T>> work, CancellationToken ct = default);
}

public sealed record PaymentResult(bool Succeeded, string? Reference, string? FailureReason);

public interface IPaymentGateway
{
    Task<PaymentResult> ChargeAsync(Guid orderId, Money amount, string paymentToken, CancellationToken ct = default);
}

/// <summary>Генерує унікальний підписаний код квитка (вміст QR-коду).</summary>
public interface ITicketCodeGenerator
{
    string Generate(Guid sessionId, SeatCode seat);
}
