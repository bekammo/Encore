namespace Encore.Modules.Inventory.Contracts;

/// <summary>
/// Limits a caller can read before it starts work, rather than discovering them
/// by being refused.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place the contracts assembly carries a value rather than a
/// shape, and it earns it. The alternative for a caller composing a multi-seat
/// request is to send five holds, have the fifth refused, and then compensate
/// the four that succeeded — four writes and four releases, during a flash sale,
/// to learn a number that was never a secret.
/// </para>
/// <para>
/// It publishes nothing new: the README states the cap, and every
/// <c>hold_cap_reached</c> response already ships it as <c>limit</c>. What this
/// removes is a second copy — a caller that hard-codes 4 is a caller that stays
/// wrong after the number changes.
/// </para>
/// <para>
/// <b>A field rather than a <c>const</c>, on purpose.</b> A <c>const</c> is
/// baked into the consumer at compile time, so an Orders assembly built against
/// one value would keep using it against an Inventory that had moved on. That is
/// only a curiosity while both live in the same process, and a real bug the day
/// <see cref="ISeatReservations"/> is served over HTTP — which is the entire
/// scenario this assembly exists for.
/// </para>
/// </remarks>
public static class SeatReservationLimits
{
    /// <summary>
    /// How many seats one client may hold at one event at once
    /// (<c>DECISIONS.md</c> 006).
    /// </summary>
    /// <remarks>
    /// Enforced by Inventory against its own count of live holds, so a caller
    /// reading this can size a request but cannot conclude the request will
    /// succeed: seats held from another tab still count, and the judgement is
    /// made against Inventory's clock, not the caller's.
    /// </remarks>
    public static readonly int MaxHoldsPerClientPerEvent = 4;
}
