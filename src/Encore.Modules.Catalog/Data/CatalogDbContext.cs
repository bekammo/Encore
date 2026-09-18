using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.Data;

/// <summary>
/// EF Core context owning the <c>catalog</c> schema. Scoped to this module:
/// no other module's tables appear here, and nothing outside Catalog takes a
/// dependency on it.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    // TODO: DbSet<Event> Events, DbSet<Venue> Venues, HasDefaultSchema("catalog").
}
