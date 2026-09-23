namespace Encore.Modules.Catalog.Models;

/// <summary>
/// Where an <see cref="Event"/> takes place. A plain POCO, not an aggregate: a venue has no
/// invariants worth a factory.
/// </summary>
public sealed class Venue
{
    public Guid Id { get; set; }

    /// <summary>Display name, as a customer would recognise it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Postal address, as one free-text line.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// How many people the venue holds. Descriptive only: sellable seats come from the event's seat map.
    /// </summary>
    public int Capacity { get; set; }
}
