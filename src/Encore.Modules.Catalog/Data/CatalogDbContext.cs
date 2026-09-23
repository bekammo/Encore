using Encore.Modules.Catalog.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.Data;

/// <summary>
/// EF Core context for the <c>catalog</c> schema. Used directly: there are no rules here
/// worth putting behind a port.
/// </summary>
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
