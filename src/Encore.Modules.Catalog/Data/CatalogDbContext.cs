using Encore.Modules.Catalog.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.Data;

/// <summary>
/// EF Core context owning the <c>catalog</c> schema. Scoped to this module:
/// no other module's tables appear here, and nothing outside Catalog takes a
/// dependency on it.
/// </summary>
/// <remarks>
/// It sits in <c>Data/</c> rather than behind a port because there is nothing
/// here worth substituting. Inventory hides its context behind
/// <c>ISeatRepository</c> so the rules can be tested against a fake in
/// microseconds; Catalog has no rules, so the same move would buy a fake of a
/// thing that only ever does one job (DECISIONS 001).
/// </remarks>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    /// <summary>The shows tickets are sold for.</summary>
    public DbSet<Event> Events => Set<Event>();

    /// <summary>The places those shows happen.</summary>
    public DbSet<Venue> Venues => Set<Venue>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(CatalogPersistence.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
