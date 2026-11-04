namespace Ticketing.Api.Core.Models;

public enum PurchaseStrategy
{
    Naive,      // read -> check -> write (race condition, oversells)
    ForUpdate   // SELECT ... FOR UPDATE (pessimistic locking)
}

// Bound from the "Purchase" config section; in Docker: env var Purchase__Strategy=ForUpdate.
public sealed class PurchaseOptions
{
    public PurchaseStrategy Strategy { get; init; } = PurchaseStrategy.Naive;
}
