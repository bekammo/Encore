namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// The result of trying to take a distributed lock: an outcome, plus the
/// ownership token when — and only when — the lock was actually taken.
/// </summary>
/// <remarks>
/// Constructed through the static members rather than the constructor, so that
/// "only an acquired lock carries a token" is a fact of the type rather than a
/// convention every call site has to remember. Mirrors the shape of the
/// Application layer's result types for the same reason.
/// </remarks>
/// <param name="Outcome">What happened.</param>
/// <param name="Token">
/// The ownership token to release with. Non-null exactly when
/// <paramref name="Outcome"/> is <see cref="LockOutcome.Acquired"/>.
/// </param>
public readonly record struct LockAcquisition(LockOutcome Outcome, string? Token)
{
    /// <summary>The caller holds the lock and is responsible for releasing it.</summary>
    public static LockAcquisition Acquired(string token) =>
        new(LockOutcome.Acquired, token);

    /// <summary>Somebody else holds it.</summary>
    public static LockAcquisition HeldByAnother { get; } =
        new(LockOutcome.HeldByAnother, null);

    /// <summary>The locking service could not answer.</summary>
    public static LockAcquisition Unavailable { get; } =
        new(LockOutcome.Unavailable, null);

    /// <summary>
    /// True when this caller took the lock. Where true, <see cref="Token"/> is
    /// non-null.
    /// </summary>
    public bool IsAcquired => Outcome is LockOutcome.Acquired;
}
