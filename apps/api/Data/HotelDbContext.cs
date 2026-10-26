using Hotel.Api.Messaging;
using Hotel.Api.Modules.Bookings;
using Hotel.Api.Modules.Rooms;
using Hotel.Api.Modules.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hotel.Api.Data;

public class HotelDbContext(DbContextOptions<HotelDbContext> options) : DbContext(options)
{
    public DbSet<RoomType> RoomTypes => Set<RoomType>();
    public DbSet<RoomNight> RoomNights => Set<RoomNight>();
    public DbSet<RoomHold> Holds => Set<RoomHold>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
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

        b.Entity<Booking>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(BookingRules.MaxReferenceLength);
            e.HasIndex(x => new { x.RoomTypeId, x.Reference }).IsUnique();
            e.HasOne<RoomType>().WithMany().HasForeignKey(x => x.RoomTypeId);
        });

        b.Entity<Voucher>(e =>
        {
            e.ToTable("Vouchers");
            e.HasKey(x => x.BookingId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}
