using Hotel.Api.Modules.Bookings;
using Xunit;

namespace Hotel.Api.Tests;

public class BookingRulesTests
{
    private static readonly DateOnly Today = new(2030, 1, 1);
    private static readonly DateOnly CheckIn = new(2030, 1, 5);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyReference_IsRejected(string? reference) =>
        Assert.NotNull(BookingRules.Validate(reference, CheckIn, 2, 1, Today));

    [Fact]
    public void EmptyDatesAndCounts_AreRejected()
    {
        Assert.NotNull(BookingRules.Validate("GUEST-1", null, 2, 1, Today));
        Assert.NotNull(BookingRules.Validate("GUEST-1", new DateOnly(2029, 12, 31), 2, 1, Today)); // in the past
        Assert.NotNull(BookingRules.Validate("GUEST-1", CheckIn, 0, 1, Today));
        Assert.NotNull(BookingRules.Validate("GUEST-1", CheckIn, 2, 0, Today));
        Assert.NotNull(BookingRules.Validate("GUEST-1", CheckIn, -1, -1, Today));
        Assert.Null(BookingRules.Validate("GUEST-1", Today, 1, 1, Today)); // check-in today is fine
    }

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(BookingRules.Validate("GUEST-1", CheckIn, BookingRules.MaxNights, BookingRules.MaxRoomsPerBooking, Today));
        Assert.NotNull(BookingRules.Validate("GUEST-1", CheckIn, BookingRules.MaxNights + 1, 1, Today));
        Assert.NotNull(BookingRules.Validate("GUEST-1", CheckIn, 2, BookingRules.MaxRoomsPerBooking + 1, Today));
        Assert.NotNull(BookingRules.Validate(new string('x', BookingRules.MaxReferenceLength + 1), CheckIn, 2, 1, Today));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as a one-night stay
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long count)
    {
        Assert.NotNull(BookingRules.Validate("GUEST-1", CheckIn, count, 1, Today));
        Assert.NotNull(BookingRules.Validate("GUEST-1", CheckIn, 2, count, Today));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // documents what the naive cast would have done
    }

    [Fact]
    public void HappyTotal_IsRateTimesNightsTimesRooms()
    {
        Assert.Equal(12_000L * 3 * 2, BookingRules.TotalCents(12_000, 3, 2)); // $720.00
        Assert.Equal(12_000L, BookingRules.TotalCents(12_000, 1, 1));
        Assert.Equal(1_000_000L * BookingRules.MaxNights * BookingRules.MaxRoomsPerBooking,
            BookingRules.TotalCents(1_000_000, BookingRules.MaxNights, BookingRules.MaxRoomsPerBooking));
    }

    [Fact]
    public void TotalArithmetic_ThrowsInsteadOfWrapping() =>
        Assert.Throws<OverflowException>(() => BookingRules.TotalCents(long.MaxValue, 2, 1));

    [Theory]
    [InlineData("POISON")]
    [InlineData("GUEST-POISON-7")]
    [InlineData("xxPOISONyy")]
    [InlineData("A-VERY-LONG-TOKEN-THAT-MERELY-CONTAINS-POISON-INSIDE")]
    public void PoisonSubstring_MatchesInsideLongerTokens(string reference) =>
        Assert.True(BookingRules.IsPoison(reference));

    [Theory]
    [InlineData("GUEST-1")]
    [InlineData("POISO")]
    [InlineData("poison")]
    public void NonPoison_DoesNotMatch(string reference) =>
        Assert.False(BookingRules.IsPoison(reference));
}
