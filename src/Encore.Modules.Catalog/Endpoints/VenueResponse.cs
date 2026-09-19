namespace Encore.Modules.Catalog.Endpoints;

/// <summary>A venue as the catalogue reports it.</summary>
/// <param name="Id">Identity, assigned at creation.</param>
/// <param name="Name">Display name.</param>
/// <param name="Address">Postal address.</param>
/// <param name="Capacity">How many people the venue holds.</param>
public sealed record VenueResponse(Guid Id, string Name, string Address, int Capacity);
