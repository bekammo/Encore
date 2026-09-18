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

    // TODO (Soundcheck, not now): outbox DbSet, and the SaveChanges override
    // that writes raised domain events into it in the same transaction as the
    // seat change. Design the outbox pattern before writing any of it.
}
