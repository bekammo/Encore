namespace Encore.Modules.Catalog.Contracts;

/// <summary>Asks what an event costs and when it may be sold.</summary>
/// <param name="EventId">The event being asked about.</param>
public sealed record EventPricingRequest(Guid EventId);
