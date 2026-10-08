namespace Ticketing.Domain.ValueObjects;

/// <summary>Місце, закріплене в межах резервування, з ціною на момент резервування.</summary>
public sealed record ReservedSeat(SeatCode Code, Money Price);
