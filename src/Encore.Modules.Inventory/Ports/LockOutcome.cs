namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// How an attempt to take a distributed lock turned out. A closed set, so a
/// caller can switch on it and choose a policy per case.
/// </summary>
/// <remarks>
/// The distinction between <see cref="HeldByAnother"/> and
/// <see cref="Unavailable"/> is the reason this type exists. A single "did not
/// get it" answer forces every caller to treat contention and an outage the
/// same way, and they are not the same: one means somebody else is working,
/// the other means nobody can tell who is. Callers that guard an invariant with
/// something else behind them can proceed on both; callers with nothing behind
/// them need to tell them apart.
/// </remarks>
public enum LockOutcome
{
    /// <summary>
    /// Somebody else holds it. The locking service answered clearly; this is
    /// ordinary contention, not a fault.
    /// </summary>
    /// <remarks>
    /// Zero deliberately. A default-constructed <see cref="LockAcquisition"/>
    /// must never read as ownership — it carries no token, so a caller that
    /// believed it held the lock would try to release one it does not have, and
    /// the safest default is the one that grants nothing.
    /// </remarks>
    HeldByAnother = 0,

    /// <summary>The lock is held by this caller, who must release it.</summary>
    Acquired = 1,

    /// <summary>
    /// The locking service could not be reached or did not answer in time, so
    /// who holds the lock is unknown. Mutual exclusion is simply not on offer
    /// right now, and the caller has to decide whether it can proceed without
    /// it.
    /// </summary>
    Unavailable = 2
}
