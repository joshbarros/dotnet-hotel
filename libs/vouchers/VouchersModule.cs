using Hotel.Shared;
using Hotel.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hotel.Vouchers;

public static class VouchersModule
{
    public static IServiceCollection AddVouchersModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Hotel") ?? throw new InvalidOperationException("Connection string 'Hotel' is not configured.");
        services.AddDbContext<VouchersDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", VouchersDbContext.SchemaName)));
        services.AddScoped<VouchersHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<VouchersDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<VouchersDbContext>>("vouchers-db", tags: ["ready"]);

        messaging.AddOutbox<VouchersDbContext>()
            .AddConsumer<VouchersHandler>(Topology.VouchersQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
