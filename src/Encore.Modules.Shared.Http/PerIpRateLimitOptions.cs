namespace Encore.Modules.Shared.Http;

public sealed class PerIpRateLimitOptions
{
    /// <summary>Off only for the load harness, whose every request comes from one address.</summary>
    public bool Enabled { get; set; } = true;

    public int PermitsPerSecond { get; set; } = 10;

    public int Burst { get; set; } = 20;
}
