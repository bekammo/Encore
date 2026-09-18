using Encore.Modules.Inventory.Ports;
using StackExchange.Redis;

namespace Encore.Modules.Inventory.Adapters.Caching;

/// <summary>
/// Redis implementation of <see cref="IDistributedLock"/>: <c>SET key token NX PX ttl</c>
/// to acquire, and a check-and-delete script to release.
/// </summary>
/// <remarks>
/// <para>
/// The release deliberately is not a bare <c>DEL</c>. If a holder stalls past the
/// TTL, Redis expires its key and someone else acquires the lock; a blind delete
/// from the stalled holder would then free a lock it no longer owns and let two
/// writers through at once. Comparing the token and deleting in one Lua script
/// makes that comparison atomic — doing it as GET-then-DEL from the client would
/// reintroduce exactly the race it is meant to close.
/// </para>
/// <para>
/// <b>Connection failures are translated, not thrown.</b> This is the same duty
/// <c>EfSeatRepository</c> performs for <c>DbUpdateConcurrencyException</c>
/// (<c>DECISIONS.md</c> 003): the port promises an outcome, so a Redis library
/// exception escaping it would both leak this adapter's vocabulary and take down
/// operations whose correctness never depended on the lock. An unreachable Redis
/// is reported as <see cref="LockOutcome.Unavailable"/> and the caller decides.
/// </para>
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

            return acquired ? LockAcquisition.Acquired(token) : LockAcquisition.HeldByAnother;
        }
        catch (Exception ex) when (IsLockServiceFailure(ex))
        {
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

            return (long)result == 1;
        }
        catch (Exception ex) when (IsLockServiceFailure(ex))
        {
            // Nothing to do and nothing to report: the key carries a TTL, so an
            // unreleasable lock frees itself within seconds. Throwing here would
            // fail an operation that has already succeeded.
            return false;
        }
    }

    /// <summary>
    /// Whether an exception means "Redis could not answer" rather than a genuine
    /// programming error.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. Swallowing every exception here would hide real bugs
    /// — a malformed script, a wrong type at a key — behind a cheerful
    /// "unavailable", and the caller would carry on believing it had merely lost
    /// a lock. <see cref="ObjectDisposedException"/> is included because a
    /// multiplexer disposed during shutdown is an availability problem from the
    /// caller's point of view, not a fault worth failing a request over.
    /// </remarks>
    private static bool IsLockServiceFailure(Exception ex) =>
        ex is RedisConnectionException or RedisTimeoutException or ObjectDisposedException;

    private static RedisKey KeyFor(string resource) => KeyPrefix + resource;
}
