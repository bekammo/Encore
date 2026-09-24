using Encore.Modules.Inventory.Ports;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Encore.Modules.Inventory.Adapters.Caching;

public sealed class RedisDistributedLock(
    IConnectionMultiplexer connection,
    ILogger<RedisDistributedLock> logger) : IDistributedLock
{
    // Not a bare DEL: a holder that stalled past its TTL would delete someone else's lock.
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
            _outage.Refused("release", resource, ex);

            return false;
        }
    }

    // Deliberately narrow: a programming error must surface, not be reported as "unavailable".
    private static bool IsLockServiceFailure(Exception ex) =>
        ex is RedisConnectionException or RedisTimeoutException or ObjectDisposedException;

    private static RedisKey KeyFor(string resource) => KeyPrefix + resource;
}
