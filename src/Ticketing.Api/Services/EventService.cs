using Ticketing.Api.Core.DTOs;
using Ticketing.Api.Core.Interfaces;
using Ticketing.Api.Core.Models;

namespace Ticketing.Api.Services;

public sealed class EventService(IEventRepository eventRepository) : IEventService
{
    public async Task<EventResponse?> GetAsync(long eventId, CancellationToken cancellationToken)
    {
        var ev = await eventRepository.GetByIdAsync(eventId, cancellationToken);
        if (ev is null)
            return null;

        var sold = await eventRepository.GetSoldCountAsync(eventId, cancellationToken);
        return new EventResponse(ev.Id, ev.Name, ev.TotalTickets, ev.AvailableTickets, sold);
    }

    public Task<PurchaseResult> PurchaseAsync(long eventId, PurchaseRequest request, CancellationToken cancellationToken)
        => eventRepository.PurchaseAsync(eventId, request.UserId, cancellationToken);

    public Task<bool> ResetAsync(long eventId, CancellationToken cancellationToken)
        => eventRepository.ResetAsync(eventId, cancellationToken);
}
