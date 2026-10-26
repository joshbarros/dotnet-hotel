using Hotel.Api.Data;
using Hotel.Api.Messaging;
using Hotel.Api.Middleware;
using Hotel.Api.Modules.Bookings;
using Hotel.Api.Modules.Rooms;
using Hotel.Api.Modules.Vouchers;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("Hotel")
    ?? throw new InvalidOperationException("Connection string 'Hotel' is not configured.");
builder.Services.AddDbContext<HotelDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<RabbitOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<BookingOptions>(builder.Configuration.GetSection("Bookings"));
builder.Services.AddSingleton<RabbitConnection>();
builder.Services.AddSingleton<KafkaLog>();

builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<RoomsHandler>();
builder.Services.AddScoped<VouchersHandler>();
builder.Services.AddScoped<BookingsHandler>();

builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService(sp => new ModuleConsumer<RoomsHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.RoomsQueue, [Topology.ReserveKey, Topology.ReleaseKey], Prefetch: 1),
    sp.GetRequiredService<ILogger<ModuleConsumer<RoomsHandler>>>()));
builder.Services.AddHostedService(sp => new ModuleConsumer<VouchersHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.VouchersQueue, [Topology.HeldKey], Prefetch: 4),
    sp.GetRequiredService<ILogger<ModuleConsumer<VouchersHandler>>>()));
builder.Services.AddHostedService(sp => new ModuleConsumer<BookingsHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.BookingsQueue,
        [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], Prefetch: 4),
    sp.GetRequiredService<ILogger<ModuleConsumer<BookingsHandler>>>()));

var app = builder.Build();

app.UseMiddleware<ExceptionHandler>();
app.MapControllers();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/api/health", () => Results.Text("Healthy"));

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<HotelDbContext>().Database.MigrateAsync();
}

app.Run();
