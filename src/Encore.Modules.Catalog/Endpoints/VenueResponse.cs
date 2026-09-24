namespace Encore.Modules.Catalog.Endpoints;

public sealed record VenueResponse(Guid Id, string Name, string Address, int Capacity);
