namespace Encore.Modules.Catalog.Models;

/// <summary>
/// Where an <see cref="Event"/> takes place. Owns the seat map's descriptive
/// side; Inventory owns whether any given seat is actually available.
/// </summary>
/// <remarks>
/// A plain persistence POCO with public setters, and deliberately not an
/// aggregate. <c>Seat</c> is constructed only through a factory (DECISIONS 005)
/// because it has invariants worth making unbypassable; a venue has none. A
/// private constructor and a <c>Create</c> method here would be the ceremony
/// DECISIONS 001 argues against — the shape would advertise rules that do not
/// exist, and the next reader would go looking for them.
/// </remarks>
public sealed class Venue
{
    /// <summary>Identity, assigned by this module when the venue is created.</summary>
    public Guid Id { get; set; }

    /// <summary>Display name, as a customer would recognise it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Postal address, kept as one free-text line. Structured address parts are
    /// a shape nothing here needs — no search, no geocoding, no distance — and
    /// splitting them now would be five columns guessing at requirements.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// How many people the venue holds. Descriptive only: the authoritative
    /// count of sellable seats is however many rows Inventory holds for an
    /// event, which is set per event by its seat map. A venue that seats 5,000
    /// can host an event selling 400.
    /// </summary>
    public int Capacity { get; set; }
}
