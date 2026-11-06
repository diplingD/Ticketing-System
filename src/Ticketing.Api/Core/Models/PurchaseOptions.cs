namespace Ticketing.Api.Core.Models;

public enum PurchaseStrategy
{
    Naive,      // read -> check -> write (race condition, oversells)
    ForUpdate,  // SELECT ... FOR UPDATE (pessimistic locking)
    Atomic,     // UPDATE ... WHERE available_tickets > 0 (check and write in one statement)
    Optimistic,         // version column: UPDATE ... WHERE version = @version, no retry on conflict
    OptimisticRetry     // the same, but on conflict retry up to 5 times with a random delay (jitter)
}

// Bound from the "Purchase" config section; in Docker: env var Purchase__Strategy=ForUpdate.
public sealed class PurchaseOptions
{
    public PurchaseStrategy Strategy { get; init; } = PurchaseStrategy.Naive;
}
