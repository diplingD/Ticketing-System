using Microsoft.AspNetCore.Mvc;
using Ticketing.Api.Core.DTOs;
using Ticketing.Api.Core.Interfaces;
using Ticketing.Api.Core.Models;

namespace Ticketing.Api.Controllers;

[ApiController]
[Route("events")]
public sealed class EventsController(IEventService eventService) : ControllerBase
{
    [HttpGet("{id:long}")]
    public async Task<ActionResult<EventResponse>> Get(long id, CancellationToken cancellationToken)
    {
        var ev = await eventService.GetAsync(id, cancellationToken);
        return ev is null ? NotFound() : Ok(ev);
    }

    [HttpPost("{id:long}/purchase")]
    public async Task<ActionResult<PurchaseResponse>> Purchase(
        long id, PurchaseRequest request, CancellationToken cancellationToken)
    {
        var result = await eventService.PurchaseAsync(id, request, cancellationToken);

        return result.Status switch
        {
            PurchaseStatus.Success => Ok(new PurchaseResponse(result.OrderId!.Value)),
            PurchaseStatus.SoldOut => Conflict("Sold out."),
            PurchaseStatus.Conflict => Conflict("Concurrent update, try again."),
            PurchaseStatus.EventNotFound => NotFound(),
            _ => throw new InvalidOperationException($"Unknown purchase status {result.Status}.")
        };
    }

    // Test-only endpoint: restores the event to its initial state between load tests.
    [HttpPost("{id:long}/reset")]
    public async Task<IActionResult> Reset(long id, CancellationToken cancellationToken)
        => await eventService.ResetAsync(id, cancellationToken) ? NoContent() : NotFound();
}
