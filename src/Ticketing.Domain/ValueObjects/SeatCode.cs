using Ticketing.Domain.Exceptions;

namespace Ticketing.Domain.ValueObjects;

/// <summary>Місце в залі: ряд і номер. Інваріант: обидва значення додатні.</summary>
public sealed record SeatCode
{
    public int Row { get; }
    public int Number { get; }

    public SeatCode(int row, int number)
    {
        if (row <= 0 || number <= 0)
            throw new DomainException("Ряд і номер місця мають бути додатними числами.");
        Row = row;
        Number = number;
    }

    public override string ToString() => $"Р{Row}-М{Number}";
}
