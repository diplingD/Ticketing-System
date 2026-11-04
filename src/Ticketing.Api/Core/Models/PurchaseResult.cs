namespace Ticketing.Api.Core.Models;

public enum PurchaseStatus
{
    Success,
    SoldOut,
    EventNotFound
}

public sealed record PurchaseResult(PurchaseStatus Status, long? OrderId = null)
{
    public static PurchaseResult Success(long orderId) => new(PurchaseStatus.Success, orderId);
    public static readonly PurchaseResult SoldOut = new(PurchaseStatus.SoldOut);
    public static readonly PurchaseResult EventNotFound = new(PurchaseStatus.EventNotFound);
}
