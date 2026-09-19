namespace Encore.Modules.Catalog.Endpoints;

/// <summary>Body of a request to create an event.</summary>
/// <param name="VenueId">The venue hosting it. Must already exist.</param>
/// <param name="Name">Display name of the show.</param>
/// <param name="StartsAt">When the show begins. Must carry a timezone.</param>
/// <param name="OnSaleAt">
/// When tickets become orderable, or <see langword="null"/> for immediately.
/// Must carry a timezone when supplied.
/// </param>
/// <param name="Price">What one seat costs.</param>
/// <param name="Currency">ISO 4217 code for <paramref name="Price"/>.</param>
public sealed record CreateEventRequest(
    Guid VenueId,
    string Name,
    DateTime StartsAt,
    DateTime? OnSaleAt,
    decimal Price,
    string Currency);
