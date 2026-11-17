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
            PurchaseStrategy.Optimistic => PurchaseOptimisticAsync(eventId, userId, cancellationToken),
            PurchaseStrategy.OptimisticRetry => PurchaseOptimisticRetryAsync(eventId, userId, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown purchase strategy {options.Value.Strategy}.")
        };

    // NAIVE on purpose
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

    // PESSIMISTIC LOCKING
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

    // ATOMIC UPDATE
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

    // OPTIMISTIC LOCKING (no retry)
    private async Task<PurchaseResult> PurchaseOptimisticAsync(long eventId, string userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. Read the value AND its version. Plain SELECT: no lock, no waiting.
        var ev = await connection.QuerySingleOrDefaultAsync<Event>(new CommandDefinition(
            "SELECT available_tickets, version FROM events WHERE id = @eventId",
            new { eventId }, transaction, cancellationToken: cancellationToken));

        // 2. Check in C#, like the naive version
        if (ev is null)
            return PurchaseResult.EventNotFound;

        if (ev.AvailableTickets <= 0)
            return PurchaseResult.SoldOut;

        // 3. Write only if nobody changed the row since our read (same version), and bump the version.
        var updatedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE events
            SET available_tickets = @newAvailable, version = version + 1
            WHERE id = @eventId AND version = @version
            """,
            new { eventId, newAvailable = ev.AvailableTickets - 1, version = ev.Version },
            transaction, cancellationToken: cancellationToken));

        // 0 rows: someone else won the race. No retry here on purpose (we want to see the effect).
        if (updatedRows == 0)
            return PurchaseResult.Conflict;

        var orderId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO orders (event_id, user_id) VALUES (@eventId, @userId) RETURNING id",
            new { eventId, userId }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return PurchaseResult.Success(orderId);
    }

    // OPTIMISTIC LOCKING WITH RETRY
    private async Task<PurchaseResult> PurchaseOptimisticRetryAsync(long eventId, string userId, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        const int maxJitterMs = 20;     // The random delay (jitter) spreads out the losers, so they don't all collide again at the same moment.

        for (var attempt = 1; ; attempt++)
        {
            var result = await PurchaseOptimisticAsync(eventId, userId, cancellationToken);

            // Success, SoldOut and EventNotFound are final answers; only a conflict is worth retrying.
            if (result.Status != PurchaseStatus.Conflict || attempt == maxAttempts)
                return result;

            // When PurchaseOptimisticAsync ended with return, the connection went back to the pool, so we don't hold it while waiting.
            await Task.Delay(Random.Shared.Next(0, maxJitterMs + 1), cancellationToken);
        }
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
