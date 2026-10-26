using System.Text.Json;
using Hotel.Api.Data;
using Hotel.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hotel.Api.Modules.Bookings;

[ApiController]
[Route("api/bookings")]
public class BookingsController(HotelDbContext db, BookingService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the booking. The room hold and the voucher follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<BookingDto>> Create(CreateBookingRequest request, CancellationToken ct)
    {
        var booking = await service.CreateAsync(request, ct);
        return Accepted($"/api/bookings/{booking.Id}", BookingDto.From(booking));
    }

    [HttpGet]
    public async Task<IEnumerable<BookingDto>> List([FromQuery] string? roomTypeCode, CancellationToken ct)
    {
        var query = db.Bookings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(roomTypeCode))
            query = query.Where(b => b.RoomTypeCode == roomTypeCode);
        return (await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)).Select(BookingDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BookingDto>> Get(Guid id, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return booking is null ? NotFound() : BookingDto.From(booking);
    }

    [HttpPost("{id:guid}/cancellation")]
    public async Task<ActionResult<BookingDto>> Cancel(Guid id, CancellationToken ct) =>
        Accepted($"/api/bookings/{id}", BookingDto.From(await service.CancelAsync(id, ct)));

    /// <summary>The Kafka event log for one booking, read from the start of the topic.</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
