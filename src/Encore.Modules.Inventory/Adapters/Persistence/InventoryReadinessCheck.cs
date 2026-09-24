using Encore.Modules.Inventory.Adapters.Messaging;
using Encore.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Persistence;

internal sealed class InventoryReadinessCheck(
    InventoryDbContext context,
    IOptions<OutboxOptions> outboxOptions) : IReadinessCheck
{
    private readonly InventoryDbContext _context = context;
    private readonly OutboxOptions _outbox = outboxOptions.Value;

    /// <inheritdoc />
    public string Name => "inventory";

    /// <inheritdoc />
    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
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

            return ReadinessResult.Ok(
                $"outbox: {pending} pending, {deadLettered} dead-lettered after {maxAttempts} attempts");
        }
        catch (Exception ex)
        {
            return ReadinessResult.Failed($"inventory database unreachable: {ex.Message}");
        }
    }
}
