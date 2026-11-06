namespace Ticketing.Api.Core.Models;

// Maps one row of the "events" table.
public sealed class Event
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public int TotalTickets { get; init; }
    public int AvailableTickets { get; init; }
    public int Version { get; init; }
}
