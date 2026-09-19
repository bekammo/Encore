using Encore.Modules.Orders.Models;

namespace Encore.Modules.Orders;

/// <summary>What confirming or cancelling an order produced.</summary>
/// <param name="Outcome">Whether the operation ran, and why not if it did not.</param>
/// <param name="Order">
/// The order as it now stands. Null only when there was no order to act on.
/// </param>
public sealed record OrderActionResult(OrderActionOutcome Outcome, Order? Order = null);
