namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>
/// Chosen names rather than CLR type names, and payloads separate from the domain events, so
/// refactoring cannot break consumers; a breaking payload change gets a new <c>.vN</c>. The
/// payloads spell their JSON names, so a consumer holding only this assembly reads what
/// Inventory writes. They are read strictly, so a member added to one needs a default (024).
/// </summary>
public static class InventoryEventTypes
{
    public static readonly string SeatHeld = "inventory.seat.held.v1";

    public static readonly string SeatReleased = "inventory.seat.released.v1";

    public static readonly string SeatSold = "inventory.seat.sold.v1";
}
