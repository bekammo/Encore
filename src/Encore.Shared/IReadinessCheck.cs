namespace Encore.Shared;

/// <summary>
/// One module's answer to "can this host do its job right now". The host resolves
/// every registration and reports them together, so it never needs to know which
/// modules have a database.
/// </summary>
public interface IReadinessCheck
{
    /// <summary>Short, stable name for this check in the response.</summary>
    string Name { get; }

    /// <summary>Runs the check. Must not throw: a failure is an answer.</summary>
    Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>What one <see cref="IReadinessCheck"/> found.</summary>
/// <param name="Ready">
/// Whether the module can serve traffic. False takes the host out of rotation, so it
/// is reserved for real outages such as an unreachable database.
/// </param>
/// <param name="Detail">
/// One line for a human, including numbers worth watching (backlog, dead letters).
/// Those do not fail the check.
/// </param>
public readonly record struct ReadinessResult(bool Ready, string Detail)
{
    public static ReadinessResult Ok(string detail) => new(true, detail);

    public static ReadinessResult Failed(string detail) => new(false, detail);
}
