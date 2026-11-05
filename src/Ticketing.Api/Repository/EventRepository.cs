using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Ticketing.Api.Core.Interfaces;
using Ticketing.Api.Core.Models;

namespace Ticketing.Api.Repository;

public sealed class EventRepository(NpgsqlDataSource dataSource, IOptions<PurchaseOptions> options) : IEventRepository
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

    public Task<PurchaseResult> PurchaseAsync(long eventId, string userId, CancellationToken cancellationToken)
        => options.Value.Strategy switch
        {
            PurchaseStrategy.Naive => PurchaseNaiveAsync(eventId, userId, cancellationToken),
            PurchaseStrategy.ForUpdate => PurchaseForUpdateAsync(eventId, userId, cancellationToken),
            PurchaseStrategy.Atomic => PurchaseAtomicAsync(eventId, userId, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown purchase strategy {options.Value.Strategy}.")
        };

    // NAIVE on purpose (Phase 1 starting point): read -> check in app -> write the computed value.
    // The transaction does NOT protect us: under READ COMMITTED two requests can both read
    // available = 1, both pass the check, and both write 0 (lost update) -> two orders for one ticket.
    private async Task<PurchaseResult> PurchaseNaiveAsync(long eventId, string userId, CancellationToken cancellationToken)
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

    // PESSIMISTIC LOCKING: the same read -> check -> write, but the read locks the row (FOR UPDATE).
    // A second transaction blocks on its SELECT until the first one commits, then reads the fresh value.
    // Correct, but every purchase (and every "sold out" answer) waits in one queue for the same row.
    private async Task<PurchaseResult> PurchaseForUpdateAsync(long eventId, string userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. Read AND lock the row until COMMIT/ROLLBACK
        var available = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT available_tickets FROM events WHERE id = @eventId FOR UPDATE",
            new { eventId }, transaction, cancellationToken: cancellationToken));

        // 2. Check: safe now, nobody else can change the row while we hold the lock
        if (available is null)
            return PurchaseResult.EventNotFound;

        if (available <= 0)
            return PurchaseResult.SoldOut;

        // 3. Write: the computed value is not stale anymore
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE events SET available_tickets = @newAvailable WHERE id = @eventId",
            new { eventId, newAvailable = available.Value - 1 }, transaction, cancellationToken: cancellationToken));

        var orderId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO orders (event_id, user_id) VALUES (@eventId, @userId) RETURNING id",
            new { eventId, userId }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);   // releases the row lock
        return PurchaseResult.Success(orderId);
    }

    // ATOMIC UPDATE: no separate read. The check (WHERE) and the write (SET) are one statement.
    // If two requests race for the last ticket, the second one waits for the row lock and then
    // Postgres re-checks the WHERE on the fresh row: available is 0, so it updates 0 rows -> sold out.
    private async Task<PurchaseResult> PurchaseAtomicAsync(long eventId, string userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Check + decrement in one step. The new value is computed by the database, not by the app.
        var updatedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE events
            SET available_tickets = available_tickets - 1
            WHERE id = @eventId AND available_tickets > 0
            """,
            new { eventId }, transaction, cancellationToken: cancellationToken));

        // 0 rows means: no free ticket OR no such event. Only on this path do we run one extra
        // plain SELECT to tell the two apart. It takes no lock and waits for nobody.
        if (updatedRows == 0)
        {
            var eventExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM events WHERE id = @eventId)",
                new { eventId }, transaction, cancellationToken: cancellationToken));

            // we dont need commit because nothing is changed
            // leaving transaction causes rollback (which releases lock the same way as commit - but update is not locking any row here anyway)
            return eventExists ? PurchaseResult.SoldOut : PurchaseResult.EventNotFound;
        }

        // We own a ticket; record the order in the same transaction.
        var orderId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO orders (event_id, user_id) VALUES (@eventId, @userId) RETURNING id",
            new { eventId, userId }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);   // releases the row lock taken by the UPDATE
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
