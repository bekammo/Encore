using Encore.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// EF Core context owning the <c>inventory</c> schema: seats, their concurrency
/// tokens, and the outbox table. Lives in Adapters because persistence is a
/// detail — the domain has never heard of it.
/// </summary>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options)
    : DbContext(options)
{
    /// <summary>The seats, and the authoritative record of their state.</summary>
    public DbSet<Seat> Seats => Set<Seat>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("inventory");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

    // TODO (Soundcheck, not now): the outbox DbSet, and the SaveChanges override
    // that drains every tracked aggregate's domain events into it in the same
    // transaction as the state change.
    //
    // This is the one place that owns the drain — DECISIONS 044 — which is why
    // EfSeatRepository.SaveAsync no longer carries a TODO of its own. Two things
    // to get right when it is written, both already reachable today:
    //
    //   - Both write paths must be covered. EfSeatRepository calls
    //     SaveChangesAsync from SaveAsync and again from AddRangeAsync, and an
    //     override is the only thing that sees both.
    //   - A rejected save leaves its outbox rows tracked as Added. All three seat
    //     handlers retry once after a lost race, so the second attempt must not
    //     write the first attempt's events alongside its own.
    //
    // Design the outbox pattern before writing any of it.
}
