namespace Ticketing.Api.Core.DTOs;

// SoldTickets is counted from the orders table, independently of AvailableTickets.
// If SoldTickets > TotalTickets, or TotalTickets - AvailableTickets != SoldTickets, we oversold.
public sealed record EventResponse(
    long Id,
    string Name,
    int TotalTickets,
    int AvailableTickets,
    int SoldTickets);
