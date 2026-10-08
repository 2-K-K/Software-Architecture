namespace Ticketing.Application.Exceptions;

/// <summary>Потрібний об'єкт (захід, сеанс, замовлення) не знайдено.</summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string entity, object key) : base($"{entity} «{key}» не знайдено.") { }
}
