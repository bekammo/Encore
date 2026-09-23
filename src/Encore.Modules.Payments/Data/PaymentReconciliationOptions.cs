namespace Encore.Modules.Payments.Data;

/// <summary>Settings for <see cref="PaymentReconciler"/>.</summary>
public sealed class PaymentReconciliationOptions
{
    public const string SectionName = "Payments:Reconciliation";

    /// <summary>
    /// Whether the reconciler runs. On by default: otherwise funds would silently stay held.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Wait between sweeps. Minutes, not seconds: asking a gateway more often does not make it answer sooner.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Attempts per sweep. Each costs a gateway round trip.
    /// </summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long an attempt must be timed out before this touches it: one hold duration, after
    /// which no confirm for it can still succeed.
    /// </summary>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromMinutes(5);
}
