using Encore.Modules.Orders.Models;

namespace Encore.Modules.Orders;

/// <summary>What a checkout attempt produced.</summary>
/// <param name="Outcome">What happened. Callers switch over this.</param>
/// <param name="Order">
/// The new order, set only when <paramref name="Outcome"/> is
/// <see cref="CheckoutOutcome.Created"/>.
/// </param>
/// <param name="Refusals">
/// The seats that could not be held, set only when <paramref name="Outcome"/> is
/// <see cref="CheckoutOutcome.SeatsUnavailable"/>. Never empty when set.
/// </param>
public sealed record CheckoutResult(
    CheckoutOutcome Outcome,
    Order? Order = null,
    IReadOnlyList<SeatRefusal>? Refusals = null)
{
    /// <summary>The order was placed.</summary>
    public static CheckoutResult Created(Order order) => new(CheckoutOutcome.Created, order);

    /// <summary>Refused for a reason that names no particular seat.</summary>
    public static CheckoutResult Refused(CheckoutOutcome outcome) => new(outcome);

    /// <summary>Refused because named seats could not be held.</summary>
    public static CheckoutResult Unavailable(IReadOnlyList<SeatRefusal> refusals) =>
        new(CheckoutOutcome.SeatsUnavailable, Refusals: refusals);
}
