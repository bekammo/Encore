namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>
/// The names Inventory publishes events under. Chosen names rather than CLR type names,
/// so refactoring cannot break consumers; a breaking payload change gets a new <c>.vN</c>.
/// </summary>
public static class InventoryEventTypes
{
    /// <summary>A seat was claimed for a client. Payload: <see cref="SeatHeldV1"/>.</summary>
    public static readonly string SeatHeld = "inventory.seat.held.v1";

    /// <summary>A seat returned to the pool. Payload: <see cref="SeatReleasedV1"/>.</summary>
    public static readonly string SeatReleased = "inventory.seat.released.v1";

    /// <summary>A seat was sold. Payload: <see cref="SeatSoldV1"/>.</summary>
    public static readonly string SeatSold = "inventory.seat.sold.v1";
}
