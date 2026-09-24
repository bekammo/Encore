using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>Answers each load from a script, one entry per call with the last repeated.</summary>
internal sealed class FakeSeatRepository : ISeatRepository
{
    private readonly IReadOnlyList<IReadOnlyList<Seat>> _loads;
    private readonly List<Exception?> _saveOutcomes = [];
    private readonly List<Guid> _liveHolds = [];

    public FakeSeatRepository(params Seat?[] loads) => _loads = ScriptOfSingles(loads);

    private FakeSeatRepository(IReadOnlyList<IReadOnlyList<Seat>> loads) => _loads = loads;

    public int LoadCalls { get; private set; }

    public int SaveCalls { get; private set; }

    public IReadOnlyCollection<Seat> LastSaved { get; private set; } = [];

    public static FakeSeatRepository Holding(params Seat[] seats) =>
        new(new IReadOnlyList<Seat>[] { seats });

    public static FakeSeatRepository Loading(params IReadOnlyList<Seat>[] loads) =>
        new(loads);

    public FakeSeatRepository WithSaveOutcomes(params Exception?[] outcomes)
    {
        _saveOutcomes.AddRange(outcomes);
        return this;
    }

    public FakeSeatRepository WithLiveHolds(int count)
    {
        _liveHolds.AddRange(Enumerable.Range(0, count).Select(_ => Guid.NewGuid()));
        return this;
    }

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

    public Task<IReadOnlyList<Guid>> FindExpiredHoldsAsync(
        DateTime utcNow,
        int limit,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The request path does not sweep expired holds.");

    public Task AddRangeAsync(
        IReadOnlyCollection<Seat> seats,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("These use cases do not create seats.");

    private IReadOnlyList<Seat> NextLoad(IReadOnlyCollection<Guid> seatIds)
    {
        var load = _loads[Math.Min(LoadCalls, _loads.Count - 1)];
        LoadCalls++;

        return [.. load.Where(seat => seatIds.Contains(seat.Id))];
    }

    private static IReadOnlyList<IReadOnlyList<Seat>> ScriptOfSingles(Seat?[] loads)
    {
        var script = loads.Length == 0 ? new Seat?[] { null } : loads;

        return [.. script.Select(seat => seat is null ? Array.Empty<Seat>() : new[] { seat })];
    }
}
