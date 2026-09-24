namespace Encore.Modules.Catalog.Endpoints;

public sealed record EventResponse(
    Guid Id,
    Guid VenueId,
    string Name,
    DateTime StartsAt,
    DateTime? OnSaleAt,
    decimal Price,
    string Currency);
