namespace Encore.Modules.Catalog.Endpoints;

/// <summary>Body of a request to create a venue.</summary>
/// <param name="Name">Display name, as a customer would recognise it.</param>
/// <param name="Address">Postal address, as one line.</param>
/// <param name="Capacity">How many people the venue holds.</param>
public sealed record CreateVenueRequest(string Name, string Address, int Capacity);
