using Encore.Modules.Notifications.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Notifications.Data;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(NotificationsPersistence.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
