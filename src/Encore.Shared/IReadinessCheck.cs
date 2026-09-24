namespace Encore.Shared;

/// <summary>
/// The host counts every registration's vote, so it never needs to know which modules have a
/// database (016).
/// </summary>
public interface IReadinessCheck
{
    string Name { get; }

    /// <summary>Must not throw: a failure is an answer.</summary>
    Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <param name="Ready">
/// False takes the host out of rotation, so reserve it for a real outage such as an unreachable
/// database. Backlog and dead-letter counts go in <c>Detail</c> and never fail the check.
/// </param>
public readonly record struct ReadinessResult(bool Ready, string Detail)
{
    public static ReadinessResult Ok(string detail) => new(true, detail);

    public static ReadinessResult Failed(string detail) => new(false, detail);
}
