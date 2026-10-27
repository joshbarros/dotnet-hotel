using Hotel.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Hotel.Bookings;

public class BookingsDbContext(DbContextOptions<BookingsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "bookings";

    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Booking>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(BookingRules.MaxReferenceLength);
            e.HasIndex(x => new { x.RoomTypeId, x.Reference }).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });
    }
}
