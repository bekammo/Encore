using Encore.Modules.Inventory.Ports;
using StackExchange.Redis;

namespace Encore.Modules.Inventory.Adapters.Caching;

/// <summary>
/// Redis implementation of <see cref="IDistributedLock"/>: <c>SET key token NX PX ttl</c>
/// to acquire, and a check-and-delete script to release.
/// </summary>
/// <remarks>
/// The release deliberately is not a bare <c>DEL</c>. If a holder stalls past the
/// TTL, Redis expires its key and someone else acquires the lock; a blind delete
/// from the stalled holder would then free a lock it no longer owns and let two
/// writers through at once. Comparing the token and deleting in one Lua script
/// makes that comparison atomic — doing it as GET-then-DEL from the client would
/// reintroduce exactly the race it is meant to close.
/// </remarks>
public sealed class RedisDistributedLock(IConnectionMultiplexer connection) : IDistributedLock
{
    /// <summary>
    /// Releases the lock only if it still holds this caller's token.
    /// Returns 1 when the key was deleted, 0 when it belonged to someone else
    /// or had already expired.
    /// </summary>
    private const string ReleaseIfOwnerScript =
        """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end
        """;

    private const string KeyPrefix = "inventory:lock:";

    private readonly IConnectionMultiplexer _connection = connection;

    /// <inheritdoc />
    public async Task<string?> TryAcquireAsync(
        string resource,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var token = Guid.NewGuid().ToString("N");

        var acquired = await _connection.GetDatabase()
            .StringSetAsync(KeyFor(resource), token, expiry: ttl, keepTtl: false, when: When.NotExists)
            .ConfigureAwait(false);

        return acquired ? token : null;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(
        string resource,
        string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _connection.GetDatabase()
            .ScriptEvaluateAsync(ReleaseIfOwnerScript, [KeyFor(resource)], [token])
            .ConfigureAwait(false);

        return (long)result == 1;
    }

    private static RedisKey KeyFor(string resource) => KeyPrefix + resource;
}
