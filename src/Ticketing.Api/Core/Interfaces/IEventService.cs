using Ticketing.Api.Core.DTOs;
using Ticketing.Api.Core.Models;

namespace Ticketing.Api.Core.Interfaces;

public interface IEventService
{
    Task<EventResponse?> GetAsync(long eventId, CancellationToken cancellationToken);
    Task<PurchaseResult> PurchaseAsync(long eventId, PurchaseRequest request, CancellationToken cancellationToken);
    Task<bool> ResetAsync(long eventId, CancellationToken cancellationToken);
}
