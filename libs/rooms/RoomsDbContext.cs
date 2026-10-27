using Hotel.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Hotel.Rooms;

public class RoomsDbContext(DbContextOptions<RoomsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "rooms";

    public DbSet<RoomType> RoomTypes => Set<RoomType>();
    public DbSet<RoomNight> RoomNights => Set<RoomNight>();
    public DbSet<RoomHold> Holds => Set<RoomHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<RoomType>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
        });

        b.Entity<RoomNight>(e =>
        {
            e.ToTable("RoomNights", t => t.HasCheckConstraint("CK_RoomNights_Remaining", "\"RoomsLeft\" >= 0 AND \"RoomsLeft\" <= \"TotalRooms\""));
            e.HasKey(x => new { x.RoomTypeId, x.Night });
            e.HasOne<RoomType>().WithMany().HasForeignKey(x => x.RoomTypeId);
        });

        b.Entity<RoomHold>(e =>
        {
            e.ToTable("RoomHolds");
            e.HasKey(x => x.BookingId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<RoomType>().WithMany().HasForeignKey(x => x.RoomTypeId);
        });
    }
}
