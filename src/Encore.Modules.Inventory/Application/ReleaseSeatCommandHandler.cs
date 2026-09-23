using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// Gives held seats back to the pool. Each seat is answered on its own, and the
/// releases are written in one transaction.
/// </summary>
/// <remarks>
/// No lock: there is no cap to protect. Releasing a seat that is already available, or
/// whose hold lapsed, succeeds, so a retry is not an error.
/// </remarks>
public sealed class ReleaseSeatCommandHandler(
    ISeatRepository seats,
    TimeProvider timeProvider)
{
    private readonly ISeatRepository _seats = seats;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>Releases one seat. A batch of one.</summary>
    public async Task<ReleaseSeatOutcome> HandleAsync(
        ReleaseSeatCommand command,
        CancellationToken cancellationToken = default)
    {
        var results = await HandleAsync(
                new ReleaseSeatsCommand(command.EventId, [command.SeatId], command.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return results[0];
    }

    /// <summary>Releases every requested seat it can.</summary>
    /// <returns>One outcome per seat, in request order.</returns>
    public async Task<IReadOnlyList<ReleaseSeatOutcome>> HandleAsync(
        ReleaseSeatsCommand command,
        CancellationToken cancellationToken = default)
    {
        SeatBatch.EnsureValid(command.SeatIds);

        var attempt = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

        if (!attempt.LostRace)
        {
            return attempt.Results;
        }

        // The retry's load discards the first attempt's changes.
        var retry = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

        if (retry.LostRace)
        {
            // Nothing else will: reload so releases that exist only in memory cannot reach a later save (011).
            await _seats.GetByIdsAsync(command.SeatIds, cancellationToken).ConfigureAwait(false);
        }

        return retry.Results;
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

        var results = new ReleaseSeatOutcome[command.SeatIds.Count];

        for (var i = 0; i < command.SeatIds.Count; i++)
        {
            if (!seats.TryGetValue(command.SeatIds[i], out var seat))
            {
                results[i] = ReleaseSeatOutcome.SeatNotFound;
                continue;
            }

            // Drop events left by a previous rejected attempt.
            seat.ClearDomainEvents();

            results[i] = TryRelease(seat, command.ClientId, utcNow);
        }

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
                    moved.Contains(seatId) ? ReleaseSeatOutcome.LostRace : results[i])],
                LostRace: true);
        }
    }

    private static ReleaseSeatOutcome TryRelease(Seat seat, Guid clientId, DateTime utcNow)
    {
        try
        {
            seat.Release(clientId, utcNow);

            return ReleaseSeatOutcome.Released;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.SeatAlreadySold)
        {
            // Sold to this client means a confirm of the same order won; a cancel uses this to back off.
            return seat.HeldByClientId == clientId
                ? ReleaseSeatOutcome.SoldToYou
                : ReleaseSeatOutcome.AlreadySold;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.NotTheHolder)
        {
            return ReleaseSeatOutcome.NotTheHolder;
        }

        // Any other reason propagates: it would mean the aggregate's contract changed.
    }

    private sealed record Attempt(IReadOnlyList<ReleaseSeatOutcome> Results, bool LostRace);
}
