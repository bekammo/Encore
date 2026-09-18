namespace Encore.Modules.Orders.Models;

/// <summary>
/// A customer's purchase: who bought what, when, and for how much. Persistence
/// POCO, not an aggregate — state transitions that matter live in Inventory
/// and Payments.
/// </summary>
public sealed class Order
{
    // TODO: Id, CustomerId, PlacedAt, Status, Total, Lines.
}
