namespace Encore.Modules.Catalog.Models;

/// <summary>A plain POCO: it has no invariants to protect (013).</summary>
public sealed class Event
{
    public Guid Id { get; set; }

    public Guid VenueId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime StartsAt { get; set; }

    public DateTime? OnSaleAt { get; set; }

    public decimal Price { get; set; }

    public string Currency { get; set; } = string.Empty;
}
