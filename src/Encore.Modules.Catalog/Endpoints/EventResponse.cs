namespace Encore.Modules.Catalog.Endpoints;

/// <summary>An event as the catalogue reports it.</summary>
/// <param name="Id">Identity, assigned at creation.</param>
/// <param name="VenueId">The venue hosting it.</param>
/// <param name="Name">Display name of the show.</param>
/// <param name="StartsAt">When the show begins, in UTC.</param>
/// <param name="OnSaleAt">When tickets became orderable, or null for immediately.</param>
/// <param name="Price">What one seat costs.</param>
/// <param name="Currency">ISO 4217 code for <paramref name="Price"/>.</param>
public sealed record EventResponse(
    Guid Id,
    Guid VenueId,
    string Name,
    DateTime StartsAt,
    DateTime? OnSaleAt,
    decimal Price,
    string Currency);
