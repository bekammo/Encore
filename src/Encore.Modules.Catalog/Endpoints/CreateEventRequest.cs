namespace Encore.Modules.Catalog.Endpoints;

public sealed record CreateEventRequest(
    Guid VenueId,
    string Name,
    DateTime StartsAt,
    DateTime? OnSaleAt,
    decimal Price,
    string Currency);
