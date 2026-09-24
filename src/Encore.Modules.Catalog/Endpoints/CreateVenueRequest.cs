namespace Encore.Modules.Catalog.Endpoints;

public sealed record CreateVenueRequest(string Name, string Address, int Capacity);
