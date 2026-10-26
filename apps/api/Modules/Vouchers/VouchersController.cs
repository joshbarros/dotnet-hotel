using Hotel.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hotel.Api.Modules.Vouchers;

public record VoucherDto(Guid BookingId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/vouchers")]
public class VouchersController(HotelDbContext db) : ControllerBase
{
    [HttpGet("{bookingId:guid}")]
    public async Task<ActionResult<VoucherDto>> Get(Guid bookingId, CancellationToken ct)
    {
        var doc = await db.Vouchers.AsNoTracking().FirstOrDefaultAsync(d => d.BookingId == bookingId, ct);
        return doc is null
            ? NotFound()
            : new VoucherDto(doc.BookingId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
