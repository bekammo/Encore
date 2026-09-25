using Encore.Modules.Inventory.Adapters.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Unhealthy only for a real outage, since that takes the host out of rotation. The backlog goes
/// in the description and never fails the check (016, 029).
/// </summary>
internal sealed class InventoryReadinessCheck(
    InventoryDbContext dbContext,
    IOptions<OutboxOptions> outboxOptions) : IHealthCheck
{
    internal const string Name = "inventory";

    private readonly InventoryDbContext _context = dbContext;
    private readonly OutboxOptions _outbox = outboxOptions.Value;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var maxAttempts = _outbox.MaxAttempts;

        try
        {
            // One aggregate query for both counts; an empty backlog returns no row.
            var backlog = await _context.OutboxMessages
                .Where(message => message.ProcessedAt == null)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Pending = group.Count(message => message.Attempts < maxAttempts),
                    DeadLettered = group.Count(message => message.Attempts >= maxAttempts)
                })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var pending = backlog?.Pending ?? 0;
            var deadLettered = backlog?.DeadLettered ?? 0;

            return HealthCheckResult.Healthy(
                $"outbox: {pending} pending, {deadLettered} dead-lettered after {maxAttempts} attempts");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"inventory database unreachable: {ex.Message}");
        }
    }
}
