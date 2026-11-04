using Ticketing.Api.Core.Models;

namespace Ticketing.Api.Core.Interfaces;

public interface IEventRepository
{
    Task<Event?> GetByIdAsync(long eventId, CancellationToken cancellationToken);
    Task<int> GetSoldCountAsync(long eventId, CancellationToken cancellationToken);
    Task<PurchaseResult> PurchaseAsync(long eventId, string userId, CancellationToken cancellationToken);
    Task<bool> ResetAsync(long eventId, CancellationToken cancellationToken);
}
