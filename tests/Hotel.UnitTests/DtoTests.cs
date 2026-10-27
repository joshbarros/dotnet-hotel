using Hotel.Bookings;
using Hotel.Rooms;
using Xunit;

namespace Hotel.UnitTests;

public class DtoTests
{
    [Fact]
    public void BookingDto_MapsEveryFieldTheStayAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            RoomTypeId = Guid.NewGuid(),
            RoomTypeCode = "R-1",
            Reference = "GUEST-1",
            CheckIn = new DateOnly(2030, 1, 5),
            Nights = 3,
            Rooms = 2,
            TotalCents = 72_000,
            Status = BookingStatus.Confirmed,
            VoucherNumber = "VC-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = BookingDto.From(booking);

        Assert.Equal(booking.Id, dto.Id);
        Assert.Equal(booking.OwnerId, dto.OwnerId);
        Assert.Equal("Confirmed", dto.Status);
        Assert.Equal(new DateOnly(2030, 1, 8), dto.CheckOut); // three nights from the 5th
        Assert.Equal(3, dto.Nights);
        Assert.Equal(2, dto.Rooms);
        Assert.Equal(72_000, dto.TotalCents);
        Assert.Equal("VC-1", dto.VoucherNumber);
    }

    [Fact]
    public void RoomTypeDto_MapsTheRoomsAndTheRate()
    {
        var roomType = new RoomType
        {
            Id = Guid.NewGuid(),
            Code = "R-1",
            Name = "Sea view",
            City = "Lisbon",
            TotalRooms = 10,
            RateCents = 12_000,
            FirstNight = new DateOnly(2030, 1, 1),
            OpenNights = 30,
        };
        var dto = RoomTypeDto.From(roomType);
        Assert.Equal(10, dto.TotalRooms);
        Assert.Equal(12_000, dto.RateCents);
        Assert.Equal(30, dto.OpenNights);
    }

    [Fact]
    public void NightDto_CarriesTheRoomsLeftOnThatNight() =>
        Assert.Equal(4, new NightDto(new DateOnly(2030, 1, 2), 10, 4).RoomsLeft);
}
