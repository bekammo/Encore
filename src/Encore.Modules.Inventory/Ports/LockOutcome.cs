namespace Encore.Modules.Inventory.Ports;

public enum LockOutcome
{
    /// <summary>Zero, so a default value never reads as ownership.</summary>
    HeldByAnother = 0,

    Acquired = 1,

    Unavailable = 2
}
