using Dapper;
using Npgsql;
using Prometheus;
using Ticketing.Api.Core.Interfaces;
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

builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IEventService, EventService>();

builder.Services.AddControllers();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

// Routing must run before UseHttpMetrics, so every metric gets the controller/action labels.
app.UseRouting();
// Counts requests and measures their duration (histogram) for every endpoint, labeled by status code.
app.UseHttpMetrics();

app.MapControllers();
app.MapHealthChecks("/health");
// GET /metrics: text format that Prometheus scrapes.
app.MapMetrics();

app.Run();
