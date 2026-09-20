using Encore.Modules.Notifications.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Notifications.Data;

/// <summary>EF Core context owning the <c>notifications</c> schema.</summary>
/// <remarks>
/// No <c>SaveChanges</c> override. Inventory's context has one because it owns an
/// outbox; this module publishes nothing and consumes instead, so there is nothing
/// to drain.
/// </remarks>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    /// <summary>What each client should be told, and when it was recorded.</summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(NotificationsPersistence.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
