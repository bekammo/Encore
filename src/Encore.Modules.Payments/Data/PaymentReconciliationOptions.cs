namespace Encore.Modules.Payments.Data;

public sealed class PaymentReconciliationOptions
{
    public const string SectionName = "Payments:Reconciliation";

    /// <summary>On by default: without the reconciler, funds would silently stay held (014).</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int BatchSize { get; set; } = 20;

    /// <summary>Grace for an attempt's own confirm to retry it before the reconciler claims it.</summary>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromMinutes(5);
}
