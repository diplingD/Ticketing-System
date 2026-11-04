using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Ticketing.Api.Utils;

public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    // If the DB is hanging and the client gives up (user closes the page, load balancer times out),
    // the token aborts the wait and immediately returns the connection to the pool for other requests.
    // Without the token, we keep waiting for the Npgsql timeout (~15-30s) and hold the connection for a response nobody will read.
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database is unreachable.", ex);
        }
    }
}
