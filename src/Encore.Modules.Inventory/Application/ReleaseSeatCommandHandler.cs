using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

public sealed class ReleaseSeatCommandHandler(
    ISeatRepository seats,
    TimeProvider timeProvider)
{
    private readonly ISeatRepository _seats = seats;
    private readonly TimeProvider _timeProvider = timeProvider;

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

        var retry = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

        if (retry.LostRace)
        {
            // Reloads only to discard the releases in memory, so no later save writes them (011).
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

        // Every state change raises an event, so these are the seats that moved.
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
            return seat.HeldByClientId == clientId
                ? ReleaseSeatOutcome.SoldToYou
                : ReleaseSeatOutcome.AlreadySold;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.NotTheHolder)
        {
            return ReleaseSeatOutcome.NotTheHolder;
        }
    }

    private sealed record Attempt(IReadOnlyList<ReleaseSeatOutcome> Results, bool LostRace);
}
