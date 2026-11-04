using System.ComponentModel.DataAnnotations;

namespace Ticketing.Api.Core.DTOs;

public sealed record PurchaseRequest([Required] string UserId);
