using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Models;

namespace Encore.Modules.Orders;

/// <param name="OpenOrderId">
/// Can be null even for <see cref="CheckoutOutcome.CheckoutAlreadyOpen"/>: the open checkout
/// may close before it is read back.
/// </param>
public sealed record CheckoutResult(
    CheckoutOutcome Outcome,
    Order? Order = null,
    IReadOnlyList<HoldSeatResponse>? Refusals = null,
    Guid? OpenOrderId = null)
{
    public static CheckoutResult Created(Order order) => new(CheckoutOutcome.Created, order);

    public static CheckoutResult Refused(CheckoutOutcome outcome) => new(outcome);

    public static CheckoutResult AlreadyOpen(Guid? openOrderId) =>
        new(CheckoutOutcome.CheckoutAlreadyOpen, OpenOrderId: openOrderId);

    public static CheckoutResult Unavailable(IReadOnlyList<HoldSeatResponse> refusals) =>
        new(CheckoutOutcome.SeatsUnavailable, Refusals: refusals);
}
