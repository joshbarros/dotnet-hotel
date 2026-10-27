using Hotel.Shared;
using Hotel.Shared.Contracts;
using Hotel.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hotel.Rooms;

public static class RoomsModule
{
    public static IServiceCollection AddRoomsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Hotel") ?? throw new InvalidOperationException("Connection string 'Hotel' is not configured.");
        services.AddDbContext<RoomsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", RoomsDbContext.SchemaName)));
        services.AddScoped<RoomsHandler>();
        services.AddScoped<IRoomTypeCatalog, RoomTypeCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<RoomsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<RoomsDbContext>>("rooms-db", tags: ["ready"]);

        messaging.AddOutbox<RoomsDbContext>()
            .AddConsumer<RoomsHandler>(Topology.RoomsQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
