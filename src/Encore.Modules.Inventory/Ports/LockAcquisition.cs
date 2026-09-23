namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// The result of trying to take a distributed lock: an outcome, plus the
/// ownership token when — and only when — the lock was actually taken.
/// </summary>
/// <param name="Outcome">What happened; each case is described on <see cref="LockOutcome"/>.</param>
/// <param name="Token">
/// The ownership token to release with. Non-null exactly when
/// <paramref name="Outcome"/> is <see cref="LockOutcome.Acquired"/>.
/// </param>
public readonly record struct LockAcquisition(LockOutcome Outcome, string? Token)
{
    /// <summary>The caller holds the lock and is responsible for releasing it.</summary>
    public static LockAcquisition Acquired(string token) =>
        new(LockOutcome.Acquired, token);

    /// <summary><see cref="LockOutcome.HeldByAnother"/>.</summary>
    public static LockAcquisition HeldByAnother { get; } =
        new(LockOutcome.HeldByAnother, null);

    /// <summary><see cref="LockOutcome.Unavailable"/>.</summary>
    public static LockAcquisition Unavailable { get; } =
        new(LockOutcome.Unavailable, null);
}
