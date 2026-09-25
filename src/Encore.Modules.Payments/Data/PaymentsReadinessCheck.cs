using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// Unhealthy only for a real outage. Unsettled attempts go in the description and never fail the
/// check (016, 029).
/// </summary>
internal sealed class PaymentsReadinessCheck(
    PaymentsDbContext dbContext,
    IOptions<PaymentReconciliationOptions> reconciliation,
    TimeProvider timeProvider) : IHealthCheck
{
    internal const string Name = "payments";

    private readonly PaymentsDbContext _context = dbContext;
    private readonly PaymentReconciliationOptions _reconciliation = reconciliation.Value;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _reconciliation.MinimumAge;

        try
        {
            var overdue = await _context.Payments
                .Where(PaymentReconciler.Overdue(cutoff))
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            return HealthCheckResult.Healthy(
                $"reconciliation: {overdue} unanswered attempts older than {_reconciliation.MinimumAge}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"payments database unreachable: {ex.Message}");
        }
    }
}
