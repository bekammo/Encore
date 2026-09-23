using Encore.Modules.Inventory.Ports;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Encore.Modules.Inventory.Adapters.Caching;

/// <summary>
/// Redis implementation of <see cref="IDistributedLock"/>: <c>SET key token NX PX ttl</c>
/// to acquire, and an atomic check-and-delete script to release.
/// </summary>
/// <remarks>
/// Release is not a bare <c>DEL</c>: a holder that stalled past its TTL would otherwise
/// delete someone else's lock. Connection failures are reported as
/// <see cref="LockOutcome.Unavailable"/> rather than thrown.
/// </remarks>
public sealed class RedisDistributedLock(
    IConnectionMultiplexer connection,
    ILogger<RedisDistributedLock> logger) : IDistributedLock
{
    /// <summary>Deletes the key only if it still holds this caller's token. Returns 1 if deleted.</summary>
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
    private readonly LockOutageLog _outage = new(logger);

    /// <inheritdoc />
    public async Task<LockAcquisition> TryAcquireAsync(
        string resource,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var token = Guid.NewGuid().ToString("N");

        try
        {
            var acquired = await _connection.GetDatabase()
                .StringSetAsync(KeyFor(resource), token, expiry: ttl, keepTtl: false, when: When.NotExists)
                .ConfigureAwait(false);

            _outage.Answered();

            return acquired ? LockAcquisition.Acquired(token) : LockAcquisition.HeldByAnother;
        }
        catch (Exception ex) when (IsLockServiceFailure(ex))
        {
            _outage.Refused("acquire", resource, ex);

            return LockAcquisition.Unavailable;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(
        string resource,
        string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var result = await _connection.GetDatabase()
                .ScriptEvaluateAsync(ReleaseIfOwnerScript, [KeyFor(resource)], [token])
                .ConfigureAwait(false);

            _outage.Answered();

            return (long)result == 1;
        }
        catch (Exception ex) when (IsLockServiceFailure(ex))
        {
            // The key has a TTL and frees itself; the work it guarded already succeeded.
            _outage.Refused("release", resource, ex);

            return false;
        }
    }

    /// <summary>
    /// Whether Redis could not answer, as opposed to a programming error. Deliberately
    /// narrow, so real bugs are not reported as "unavailable".
    /// </summary>
    private static bool IsLockServiceFailure(Exception ex) =>
        ex is RedisConnectionException or RedisTimeoutException or ObjectDisposedException;

    private static RedisKey KeyFor(string resource) => KeyPrefix + resource;
}
