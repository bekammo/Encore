namespace Encore.Modules.Inventory.Ports;

/// <param name="Token">
/// Non-null exactly when <paramref name="Outcome"/> is <see cref="LockOutcome.Acquired"/>.
/// </param>
public readonly record struct LockAcquisition(LockOutcome Outcome, string? Token)
{
    public static LockAcquisition Acquired(string token) =>
        new(LockOutcome.Acquired, token);

    public static LockAcquisition HeldByAnother { get; } =
        new(LockOutcome.HeldByAnother, null);

    public static LockAcquisition Unavailable { get; } =
        new(LockOutcome.Unavailable, null);
}
