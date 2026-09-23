using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The "I don't want these seats after all" use case: hand held seats back to the
/// pool. Sequences the ports the same way the other seat writes do — load, let
/// each aggregate decide, persist them together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each seat is answered on its own.</b> A seat that cannot be released does
/// not keep the others held, which is how cancelling behaved when the seats went
/// back one at a time (034). The batch only makes it one round trip (076).
/// </para>
/// <para>
/// <b>No lock.</b> Releasing touches rows whose tokens settle any race, and there
/// is no cap to protect: releasing gives capacity back rather than consuming it.
/// </para>
/// <para>
/// <b>Releasing a seat you are not holding is a success.</b> The caller wanted
/// not to be holding this seat, and they are not — whether their hold lapsed
/// while the request was in flight, or the request is a duplicate of one that
/// already worked. Refusing would turn a retry into an error for a state that
/// already holds, which is the same reasoning that makes a re-hold and a
/// re-purchase idempotent.
/// </para>
/// </remarks>
public sealed class ReleaseSeatCommandHandler(
    ISeatRepository seats,
    TimeProvider timeProvider)
{
    private readonly ISeatRepository _seats = seats;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Attempts to release <see cref="ReleaseSeatCommand.SeatId"/> on behalf of
    /// <see cref="ReleaseSeatCommand.ClientId"/>.
    /// </summary>
    /// <returns>The outcome. Refusals are returned, not thrown.</returns>
    public async Task<ReleaseSeatResult> HandleAsync(
        ReleaseSeatCommand command,
        CancellationToken cancellationToken = default)
    {
        var results = await HandleAsync(
                new ReleaseSeatsCommand(command.EventId, [command.SeatId], command.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return results[0];
    }

    /// <summary>
    /// Attempts to release every seat in <see cref="ReleaseSeatsCommand.SeatIds"/>
    /// on behalf of <see cref="ReleaseSeatsCommand.ClientId"/>.
    /// </summary>
    /// <returns>One outcome per seat, in the order the seats were asked for.</returns>
    public async Task<IReadOnlyList<ReleaseSeatResult>> HandleAsync(
        ReleaseSeatsCommand command,
        CancellationToken cancellationToken = default)
    {
        SeatBatch.EnsureValid(command.SeatIds);

        var attempt = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

        return attempt.LostRace
            ? (await AttemptAsync(command, cancellationToken).ConfigureAwait(false)).Results
            : attempt.Results;
    }

    private async Task<Attempt> AttemptAsync(
        ReleaseSeatsCommand command,
        CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        var loaded = await _seats.GetByIdsAsync(command.SeatIds, cancellationToken).ConfigureAwait(false);

        var seats = loaded
            .Where(seat => seat.EventId == command.EventId)
            .ToDictionary(seat => seat.Id);

        var results = new ReleaseSeatResult[command.SeatIds.Count];

        for (var i = 0; i < command.SeatIds.Count; i++)
        {
            if (!seats.TryGetValue(command.SeatIds[i], out var seat))
            {
                results[i] = ReleaseSeatResult.SeatNotFound;
                continue;
            }

            // A rejected attempt may have left events on this instance describing a
            // release that never happened.
            seat.ClearDomainEvents();

            results[i] = TryRelease(seat, command.ClientId, utcNow);
        }

        // Every state change a seat makes raises an event, so these are the seats
        // this attempt actually moved.
        var changed = seats.Values.Where(seat => seat.DomainEvents.Count > 0).ToList();

        if (changed.Count is 0)
        {
            return new Attempt(results, LostRace: false);
        }

        try
        {
            await _seats.SaveAsync(changed, cancellationToken).ConfigureAwait(false);

            return new Attempt(results, LostRace: false);
        }
        catch (ConcurrentSeatModificationException)
        {
            // Nothing was written, so every seat this attempt moved is still held.
            var moved = changed.Select(seat => seat.Id).ToHashSet();

            return new Attempt(
                [.. command.SeatIds.Select((seatId, i) =>
                    moved.Contains(seatId) ? ReleaseSeatResult.LostRace : results[i])],
                LostRace: true);
        }
    }

    private static ReleaseSeatResult TryRelease(Seat seat, Guid clientId, DateTime utcNow)
    {
        try
        {
            seat.Release(clientId, utcNow);

            return ReleaseSeatResult.Released;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.SeatAlreadySold)
        {
            return ReleaseSeatResult.AlreadySold;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.NotTheHolder)
        {
            return ReleaseSeatResult.NotTheHolder;
        }

        // Release() refuses for exactly the two reasons handled above. A third
        // would mean the aggregate's contract moved without this handler being
        // told, and it is left to propagate rather than be swallowed.
    }

    private sealed record Attempt(IReadOnlyList<ReleaseSeatResult> Results, bool LostRace);
}
