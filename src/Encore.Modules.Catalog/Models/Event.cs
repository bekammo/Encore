namespace Encore.Modules.Catalog.Models;

/// <summary>
/// A show that tickets are sold for. A plain persistence POCO: this module is
/// a read-mostly catalogue, so there are no invariants to protect.
/// </summary>
public sealed class Event
{
    // TODO: Id, Name, VenueId, StartsAt, OnSaleAt.
}
