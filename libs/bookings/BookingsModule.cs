using Hotel.Shared;
using Hotel.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hotel.Bookings;

public static class BookingsModule
{
    public static IServiceCollection AddBookingsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Hotel") ?? throw new InvalidOperationException("Connection string 'Hotel' is not configured.");
        services.AddDbContext<BookingsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", BookingsDbContext.SchemaName)));
        services.Configure<BookingOptions>(config.GetSection("Bookings"));
        services.AddScoped<BookingService>();
        services.AddScoped<BookingsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<BookingsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<BookingsDbContext>>("bookings-db", tags: ["ready"]);

        messaging.AddOutbox<BookingsDbContext>()
            .AddConsumer<BookingsHandler>(Topology.BookingsQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
