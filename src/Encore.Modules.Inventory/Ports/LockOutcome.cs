namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// How a lock attempt turned out. Contention and an outage are different answers, because
/// a caller with nothing else guarding it must treat them differently.
/// </summary>
public enum LockOutcome
{
    /// <summary>Somebody else holds it. Zero, so a default value never reads as ownership.</summary>
    HeldByAnother = 0,

    /// <summary>The lock is held by this caller, who must release it.</summary>
    Acquired = 1,

    /// <summary>
    /// The lock service could not be reached, so who holds the lock is unknown.
    /// </summary>
    Unavailable = 2
}
