namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>
/// The names Inventory publishes its events under. A consumer matches on one of
/// these strings, never on a CLR type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a name and not a type name.</b> The outbox payload is a published wire
/// format, and a CLR type name would make the domain's namespace part of it — so
/// moving <c>SeatSold</c> between folders, or renaming the assembly at extraction,
/// would break every consumer that had already stored rows. A name chosen on
/// purpose is stable because nothing else depends on it.
/// </para>
/// <para>
/// <b>The <c>.v1</c> suffix is the versioning story and it is deliberately
/// cheap.</b> A breaking change to a payload gets a new name and a new record
/// beside the old one, and the dispatcher carries both until the last consumer has
/// moved. Rows already in the table keep meaning what they meant when they were
/// written, which is the property an append-only log exists for (007).
/// </para>
/// <para>
/// <b><c>static readonly</c>, not <c>const</c>, for 020's reason.</b> A const is
/// copied into the consumer's assembly at compile time, so a consumer built
/// against one spelling would keep using it after Inventory moved on, with nothing
/// to notice. That is a curiosity while both live in one process and a real bug the
/// day this module is served over HTTP — which is the scenario this assembly exists
/// for.
/// </para>
/// </remarks>
public static class InventoryEventTypes
{
    /// <summary>A seat was claimed for a client. Payload: <see cref="SeatHeldV1"/>.</summary>
    public static readonly string SeatHeld = "inventory.seat.held.v1";

    /// <summary>A seat returned to the pool. Payload: <see cref="SeatReleasedV1"/>.</summary>
    public static readonly string SeatReleased = "inventory.seat.released.v1";

    /// <summary>A seat was sold. Payload: <see cref="SeatSoldV1"/>.</summary>
    public static readonly string SeatSold = "inventory.seat.sold.v1";
}
