using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Prometheus;
using Ticketing.Api.Core.Interfaces;
using Ticketing.Api.Core.Models;
using Ticketing.Api.Repository;
using Ticketing.Api.Services;
using Ticketing.Api.Utils;

// Map snake_case columns (available_tickets) to PascalCase properties (AvailableTickets).
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Db")
    ?? throw new InvalidOperationException("Connection string 'Db' is not configured.");

// One shared data source for the whole app; it owns the connection pool.
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

builder.Services.Configure<PurchaseOptions>(builder.Configuration.GetSection("Purchase"));
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IEventService, EventService>();

builder.Services.AddControllers();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

// Log the active purchase strategy at startup, so every load test run shows what was measured.
app.Logger.LogInformation("Purchase strategy: {Strategy}",
    app.Services.GetRequiredService<IOptions<PurchaseOptions>>().Value.Strategy);

// Routing must run before UseHttpMetrics, so every metric gets the controller/action labels.
app.UseRouting();
// Counts requests and measures their duration (histogram) for every endpoint, labeled by status code.
app.UseHttpMetrics();

app.MapControllers();
app.MapHealthChecks("/health");
// GET /metrics: text format that Prometheus scrapes.
app.MapMetrics();

app.Run();
