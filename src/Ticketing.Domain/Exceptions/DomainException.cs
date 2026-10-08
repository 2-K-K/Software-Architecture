namespace Ticketing.Domain.Exceptions;

/// <summary>Порушення інваріанта або доменного правила.</summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}
