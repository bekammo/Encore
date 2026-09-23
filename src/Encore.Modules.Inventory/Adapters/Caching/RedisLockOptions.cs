namespace Encore.Modules.Inventory.Adapters.Caching;

/// <summary>Settings for the Redis lock.</summary>
public sealed class RedisLockOptions
{
    public const string SectionName = "Inventory:RedisLock";

    /// <summary>
    /// How long the lock stops asking Redis after Redis could not answer. Every caller in that
    /// window is told "unavailable" at once and proceeds without the lock, as it would have
    /// anyway. Zero asks Redis every time.
    /// </summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(1);
}
