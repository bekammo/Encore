using Encore.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Payments.Data;

internal sealed class PaymentsReadinessCheck(
    PaymentsDbContext context,
    IOptions<PaymentReconciliationOptions> reconciliation,
    TimeProvider timeProvider) : IReadinessCheck
{
    private readonly PaymentsDbContext _context = context;
    private readonly PaymentReconciliationOptions _reconciliation = reconciliation.Value;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public string Name => "payments";

    /// <inheritdoc />
    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _reconciliation.MinimumAge;

        try
        {
            var overdue = await _context.Payments
                .Where(PaymentReconciler.Overdue(cutoff))
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            return ReadinessResult.Ok(
                $"reconciliation: {overdue} unanswered attempts older than {_reconciliation.MinimumAge}");
        }
        catch (Exception ex)
        {
            return ReadinessResult.Failed($"payments database unreachable: {ex.Message}");
        }
    }
}
