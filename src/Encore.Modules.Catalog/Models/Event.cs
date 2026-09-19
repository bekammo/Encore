namespace Encore.Modules.Catalog.Models;

/// <summary>
/// A show that tickets are sold for. A plain persistence POCO: this module is
/// a read-mostly catalogue, so there are no invariants to protect.
/// </summary>
/// <remarks>
/// Not an aggregate, and deliberately so — see <see cref="Venue"/> for why the
/// factory-only construction of <c>Seat</c> does not apply to these types.
/// </remarks>
public sealed class Event
{
    /// <summary>Identity, assigned by this module when the event is created.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The venue hosting it. A plain id rather than a navigation property:
    /// nothing in this module traverses from an event to its venue and back,
    /// and a navigation would invite a lazy-loading N+1 the first time a list
    /// endpoint rendered one.
    /// </summary>
    public Guid VenueId { get; set; }

    /// <summary>Display name of the show.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the show begins. Always UTC.</summary>
    public DateTime StartsAt { get; set; }

    /// <summary>
    /// When tickets become orderable, or <see langword="null"/> for on sale
    /// immediately. Orders refuses a checkout before this instant (DECISIONS
    /// 026) and stops caring after it — sales stay open once the show has
    /// started, because walk-up sales are a real thing.
    /// </summary>
    /// <remarks>
    /// Nullable rather than defaulting to <see cref="DateTime.MinValue"/>: "no
    /// gate" and "a gate that opened in the year 1" are different statements,
    /// and only one of them is honest about what the operator said.
    /// </remarks>
    public DateTime? OnSaleAt { get; set; }

    /// <summary>
    /// What one seat costs. Catalog is authoritative for price; Orders copies
    /// this onto the order line at checkout and never re-reads it, so changing
    /// it here does not rewrite what anybody already agreed to pay.
    /// </summary>
    /// <remarks>
    /// One price for the whole event. Price bands per section are deferred
    /// (DECISIONS 012's open note) until a seat map carries structure at all.
    /// </remarks>
    public decimal Price { get; set; }

    /// <summary>
    /// ISO 4217 code for <see cref="Price"/>. Carried per event rather than set
    /// system-wide, because no system-wide currency is written down anywhere
    /// and picking one silently would be inventing a rule. One event per order
    /// means one currency per order, so nothing in this system ever converts.
    /// </summary>
    public string Currency { get; set; } = string.Empty;
}
