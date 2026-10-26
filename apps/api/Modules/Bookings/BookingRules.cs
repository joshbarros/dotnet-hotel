namespace Hotel.Api.Modules.Bookings;

public static class BookingRules
{
    public const int MaxNights = 14;
    public const int MaxRoomsPerBooking = 3;
    public const int MaxReferenceLength = 40;
    public const string PoisonToken = "POISON";

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, DateOnly? checkIn, long nights, long rooms, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Guest reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Guest reference must be at most {MaxReferenceLength} characters.";
        if (checkIn is null)
            return "Check-in date is required.";
        if (checkIn < today)
            return "Check-in cannot be in the past.";
        if (nights < 1)
            return "A stay needs at least 1 night.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (nights > MaxNights)
            return $"A stay is at most {MaxNights} nights.";
        if (rooms < 1)
            return "A booking needs at least 1 room.";
        if (rooms > MaxRoomsPerBooking)
            return $"A booking is at most {MaxRoomsPerBooking} rooms.";
        return null;
    }

    /// <summary>Case-sensitive substring match: a longer token that merely contains the poison matches too.</summary>
    public static bool IsPoison(string reference) => reference.Contains(PoisonToken, StringComparison.Ordinal);

    /// <summary>Nightly rate times nights times rooms, in cents. Overflow throws instead of wrapping.</summary>
    public static long TotalCents(long rateCents, int nights, int rooms) => checked(rateCents * nights * rooms);
}
