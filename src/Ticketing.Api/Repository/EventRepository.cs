using Dapper;
using Npgsql;
using Ticketing.Api.Core.Interfaces;
using Ticketing.Api.Core.Models;

namespace Ticketing.Api.Repository;

public sealed class EventRepository(NpgsqlDataSource dataSource) : IEventRepository
{
    public async Task<Event?> GetByIdAsync(long eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, total_tickets, available_tickets
            FROM events
            WHERE id = @eventId
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Event>(
            new CommandDefinition(sql, new { eventId }, cancellationToken: cancellationToken));
    }

    public async Task<int> GetSoldCountAsync(long eventId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*)::int FROM orders WHERE event_id = @eventId";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { eventId }, cancellationToken: cancellationToken));
    }

    // NAIVE on purpose (Phase 1 starting point): read -> check in app -> write the computed value.
    // The transaction does NOT protect us: under READ COMMITTED two requests can both read
    // available = 1, both pass the check, and both write 0 (lost update) -> two orders for one ticket.
    public async Task<PurchaseResult> PurchaseAsync(long eventId, string userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. Read
        var available = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT available_tickets FROM events WHERE id = @eventId",
            new { eventId }, transaction, cancellationToken: cancellationToken));

        // 2. Check in application code, on a value that may already be stale.
        //    Returning early disposes the transaction, which rolls it back.
        if (available is null)
            return PurchaseResult.EventNotFound;

        if (available <= 0)
            return PurchaseResult.SoldOut;

        // 3. Write the value computed in the app (not "available_tickets - 1" in SQL).
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE events SET available_tickets = @newAvailable WHERE id = @eventId",
            new { eventId, newAvailable = available.Value - 1 }, transaction, cancellationToken: cancellationToken));

        var orderId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO orders (event_id, user_id) VALUES (@eventId, @userId) RETURNING id",
            new { eventId, userId }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return PurchaseResult.Success(orderId);
    }

    // Test helper: deletes orders and restores the counter, so load tests can be repeated without "down -v".
    public async Task<bool> ResetAsync(long eventId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM orders WHERE event_id = @eventId",
            new { eventId }, transaction, cancellationToken: cancellationToken));

        var updated = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE events SET available_tickets = total_tickets WHERE id = @eventId",
            new { eventId }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return updated > 0;
    }
}
