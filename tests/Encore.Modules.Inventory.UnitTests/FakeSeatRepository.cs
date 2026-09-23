using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The seat store for the handlers' unit tests. Answers each load from a script, one entry per
/// call with the last repeated, and each save from a list of outcomes. Shared by the three
/// handlers' tests, so a change to <see cref="ISeatRepository"/> is made once.
/// </summary>
internal sealed class FakeSeatRepository : ISeatRepository
{
    private readonly IReadOnlyList<IReadOnlyList<Seat>> _loads;
    private readonly List<Exception?> _saveOutcomes = [];
    private readonly List<Guid> _liveHolds = [];

    /// <summary>One seat per load, in order; a null entry is a load that finds nothing.</summary>
    public FakeSeatRepository(params Seat?[] loads) => _loads = ScriptOfSingles(loads);

    private FakeSeatRepository(IReadOnlyList<IReadOnlyList<Seat>> loads, bool scripted) => _loads = loads;

    /// <summary>Loads of either kind: a batch load, or a hold's load.</summary>
    public int GetByIdCalls { get; private set; }

    public int SaveCalls { get; private set; }

    /// <summary>The seats handed to the most recent save.</summary>
    public IReadOnlyCollection<Seat> LastSaved { get; private set; } = [];

    /// <summary>Every call returns these seats.</summary>
    public static FakeSeatRepository Holding(params Seat[] seats) =>
        new(new IReadOnlyList<Seat>[] { seats }, scripted: true);

    /// <summary>One batch per call, the last repeated.</summary>
    public static FakeSeatRepository Loading(params IReadOnlyList<Seat>[] loads) =>
        new(loads, scripted: true);

    /// <summary>One entry per expected save: an exception to throw, or null to succeed.</summary>
    public FakeSeatRepository WithSaveOutcomes(params Exception?[] outcomes)
    {
        _saveOutcomes.AddRange(outcomes);
        return this;
    }

    /// <summary>This client already holds this many other seats at the event.</summary>
    public FakeSeatRepository WithLiveHolds(int count)
    {
        _liveHolds.AddRange(Enumerable.Range(0, count).Select(_ => Guid.NewGuid()));
        return this;
    }

    /// <summary>This client is holding this particular seat live.</summary>
    public FakeSeatRepository WithLiveHoldOn(Guid seatId)
    {
        _liveHolds.Add(seatId);
        return this;
    }

    public Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The handlers load seats in batches.");

    public Task<IReadOnlyList<Seat>> GetByIdsAsync(
        IReadOnlyCollection<Guid> seatIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(NextLoad(seatIds));

    public Task<SeatsForHold> GetForHoldAsync(
        IReadOnlyCollection<Guid> seatIds,
        Guid clientId,
        Guid eventId,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SeatsForHold(NextLoad(seatIds), _liveHolds));

    public Task SaveAsync(Seat seat, CancellationToken cancellationToken = default) =>
        SaveAsync([seat], cancellationToken);

    public Task SaveAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default)
    {
        var outcome = SaveCalls < _saveOutcomes.Count ? _saveOutcomes[SaveCalls] : null;
        SaveCalls++;
        LastSaved = seats;

        return outcome is null ? Task.CompletedTask : Task.FromException(outcome);
    }

    /// <summary>The sweep's query; no handler calls it, so it throws.</summary>
    public Task<IReadOnlyList<Guid>> FindExpiredHoldsAsync(
        DateTime utcNow,
        int limit,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The request path does not sweep expired holds.");

    /// <summary>Seats already exist on this path; creating them is a different use case.</summary>
    public Task AddRangeAsync(
        IReadOnlyCollection<Seat> seats,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("These use cases do not create seats.");

    private IReadOnlyList<Seat> NextLoad(IReadOnlyCollection<Guid> seatIds)
    {
        var load = _loads[Math.Min(GetByIdCalls, _loads.Count - 1)];
        GetByIdCalls++;

        return [.. load.Where(seat => seatIds.Contains(seat.Id))];
    }

    private static IReadOnlyList<IReadOnlyList<Seat>> ScriptOfSingles(Seat?[] loads)
    {
        var script = loads.Length == 0 ? new Seat?[] { null } : loads;

        return [.. script.Select(seat => seat is null ? Array.Empty<Seat>() : new[] { seat })];
    }
}
