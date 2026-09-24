using System.Collections.Concurrent;
using Encore.Modules.Inventory.Ports;
using Npgsql;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// A session-level advisory lock, so each held lock keeps its own connection until release.
/// The TTL is ignored: a crashed holder's lock dies with its connection. A key collision
/// costs a spurious "in flight" refusal, never a breach.
/// </summary>
public sealed class PostgresAdvisoryLock(NpgsqlDataSource dataSource) : IDistributedLock
{
    private const string TryLockSql = "SELECT pg_try_advisory_lock(hashtextextended($1, 0))";
    private const string UnlockSql = "SELECT pg_advisory_unlock(hashtextextended($1, 0))";

    private readonly NpgsqlDataSource _dataSource = dataSource;

    private readonly ConcurrentDictionary<string, NpgsqlConnection> _held = new();

    /// <inheritdoc />
    public async Task<LockAcquisition> TryAcquireAsync(
        string resource,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        NpgsqlConnection? connection = null;

        try
        {
            connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Not cancellable: a cancel landing after the server took the lock would return a
            // locked session to the pool. One round trip; the open above honours the token.
            if (!await ScalarAsync(connection, TryLockSql, resource, CancellationToken.None).ConfigureAwait(false))
            {
                await connection.DisposeAsync().ConfigureAwait(false);

                return LockAcquisition.HeldByAnother;
            }

            var token = Guid.NewGuid().ToString("N");
            _held[token] = connection;

            return LockAcquisition.Acquired(token);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            return LockAcquisition.Unavailable;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(
        string resource,
        string token,
        CancellationToken cancellationToken = default)
    {
        if (!_held.TryRemove(token, out var connection))
        {
            return false;
        }

        await using (connection.ConfigureAwait(false))
        {
            try
            {
                return await ScalarAsync(connection, UnlockSql, resource, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                // A broken connection is not pooled again, and its session's locks die with it.
                return false;
            }
        }
    }

    private static async Task<bool> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = resource });

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
