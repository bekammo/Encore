using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The parts of seat locking that are the same wherever it happens: how long a
/// lock lives, what the resources are called, and how one is given back.
/// </summary>
/// <remarks>
/// <para>
/// <b>One copy because a second copy is a second thing to keep correct.</b> That
/// is 017's criterion, and by its own terms this is the case that qualifies while
/// the migrators do not: a migrator is inert, so a bug in one copy cannot be a
/// bug in another. These are not inert. <see cref="ForSeat"/> in particular is
/// the one where drift is a correctness bug rather than untidiness — two handlers
/// spelling the same seat's resource differently would take different locks and
/// believe they held the same one, which is mutual exclusion silently not
/// happening at the exact moment it is wanted. See <c>DECISIONS.md</c> 046.
/// </para>
/// <para>
/// <b>Here rather than beside the port.</b> <c>Ports/</c> states a contract that
/// adapters implement; none of this is part of that contract, and
/// <see cref="ReleaseIfHeldAsync"/> in particular is a caller's discipline rather
/// than an implementor's obligation. Every caller today is in this layer, and the
/// expired-hold sweep will be too.
/// </para>
/// </remarks>
internal static class SeatLocks
{
    /// <summary>
    /// How long a lock survives if it is never released.
    /// </summary>
    /// <remarks>
    /// Sized to one write attempt, never to a business-level window: it is a
    /// safety net for a process that died mid-write, not a booking. A hold lasts
    /// five minutes and this lasts five seconds, and the two numbers being
    /// unrelated is the point — <see cref="Domain.Seat.HoldDuration"/> is the
    /// domain's, this is the infrastructure's.
    /// </remarks>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    /// <summary>Names the lock that serialises writes to one seat.</summary>
    public static string ForSeat(Guid seatId) => $"seat:{seatId}";

    /// <summary>
    /// Names the lock that serialises one client against themselves across the
    /// several seats the hold cap counts.
    /// </summary>
    /// <remarks>
    /// Keyed on the client <i>and</i> the event because the cap is per event
    /// (006): a client checking out of two shows at once is not contending with
    /// themselves, and keying on the client alone would serialise them for no
    /// reason.
    /// </remarks>
    public static string ForClient(Guid clientId, Guid eventId) =>
        $"client:{clientId}:event:{eventId}";

    /// <summary>
    /// Releases a lock the caller took, if it took one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately takes no <see cref="CancellationToken"/> and passes
    /// <see cref="CancellationToken.None"/>. This runs from a <c>finally</c>
    /// after the write has already happened, so a client that disconnected
    /// mid-request would otherwise cancel the release and strand the lock —
    /// holding every other caller off the seat until the TTL runs out, on the one
    /// path where releasing promptly matters most. Not accepting a token is what
    /// stops a caller passing the wrong one; the port documents the rule, and this
    /// is the rule as code.
    /// </para>
    /// <para>
    /// A <see cref="LockAcquisition"/> that was never acquired carries no token
    /// and is a no-op here, which is why every call site can sit in a
    /// <c>finally</c> without first asking whether there is anything to give back.
    /// </para>
    /// </remarks>
    public static async Task ReleaseIfHeldAsync(
        this IDistributedLock distributedLock,
        string resource,
        LockAcquisition acquisition)
    {
        if (acquisition.Token is { } token)
        {
            await distributedLock
                .ReleaseAsync(resource, token, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
