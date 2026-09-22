using Encore.Modules.Payments.Models;
using Encore.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// Payments' answer to <c>/health/ready</c>: can it reach its database, and how many
/// attempts is the reconciler still carrying. <c>DECISIONS.md</c> 070.
/// </summary>
/// <remarks>
/// <para>
/// <b>The number worth surfacing here is the one 064 had to run <c>psql</c> to
/// find.</b> A timed-out attempt older than <see cref="PaymentReconciliationOptions.MinimumAge"/>
/// is one the sweep should have settled and has not — either because the gateway
/// keeps failing to answer, or because nothing is running the sweep at all. Both are
/// silent today, and both mean a customer's funds may be held against an order that
/// cannot be paid for again while the row stays live (030).
/// </para>
/// <para>
/// <b>It does not fail the check</b>, for <c>InventoryReadinessCheck</c>'s reason: an
/// unsettled attempt is a thing to look at, not a reason to stop serving. The only
/// failure this reports is a database it cannot read.
/// </para>
/// </remarks>
internal sealed class PaymentsReadinessCheck(
    PaymentsDbContext context,
    IOptions<PaymentReconciliationOptions> reconciliation,
    TimeProvider clock) : IReadinessCheck
{
    private readonly PaymentsDbContext _context = context;
    private readonly PaymentReconciliationOptions _reconciliation = reconciliation.Value;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc />
    public string Name => "payments";

    /// <inheritdoc />
    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - _reconciliation.MinimumAge;

        try
        {
            var overdue = await _context.Payments
                .Where(payment => payment.Status == PaymentStatus.TimedOut && payment.ResolvedAt <= cutoff)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            return ReadinessResult.Ok(
                $"reconciliation: {overdue} timed-out attempts older than {_reconciliation.MinimumAge}");
        }
        catch (Exception ex)
        {
            return ReadinessResult.Failed($"payments database unreachable: {ex.Message}");
        }
    }
}
