using Encore.Modules.Orders.Models;

namespace Encore.Modules.Orders;

public sealed record OrderActionResult(OrderActionOutcome Outcome, Order? Order = null);
