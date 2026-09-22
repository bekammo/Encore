using Encore.Modules.Inventory.Adapters.Messaging;
using Encore.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Inventory's answer to <c>/health/ready</c>: can it reach its database, and is
/// anything stuck in the outbox. <c>DECISIONS.md</c> 070.
/// </summary>
/// <remarks>
/// <para>
/// <b>The count is the ping.</b> A separate "can you connect" probe would be a second
/// round trip proving something this query proves on its way past, and a connection
/// that opens but cannot read is not a database anybody can serve from.
/// </para>
/// <para>
/// <b>A dead letter does not make this module unready, and that is the whole
/// judgement in this file.</b> 051 asked for a dead-letter count somewhere visible
/// and nothing surfaced one, so a message that stopped being retried was invisible
/// until somebody ran a query — which in 064 is exactly what happened. But taking a
/// host out of rotation because one <c>SeatSold</c> never reached Notifications would
/// convert a message nobody read into a request path nobody can reach. It is
/// reported, loudly, in the detail line; it is not a failure.
/// </para>
/// </remarks>
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
            // One round trip for both numbers, over the partial index that already
            // covers undelivered rows (051). GroupBy(_ => 1) is the shape that makes
            // EF emit a single aggregate rather than two queries; an empty backlog
            // produces no row at all, which is the zero case below.
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
            // Deliberately broad. This method's contract is that it answers rather
            // than throws, because an exception escaping here would turn the
            // readiness endpoint itself into the thing that is down.
            return ReadinessResult.Failed($"inventory database unreachable: {ex.Message}");
        }
    }
}
