using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Notifications.Data;
using Encore.Shared;
using Encore.Modules.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Encore.Modules.Notifications;

/// <summary>
/// The Notifications module's composition seam.
/// </summary>
/// <remarks>
/// There is no <c>MapNotificationsModule</c>: the module serves no routes. Its whole inbound
/// surface is the handler registration below, which Inventory's dispatcher resolves without
/// either module naming the other's implementation.
/// </remarks>
public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<NotificationsDbContext>(options =>
            options.UseNotificationsNpgsql(
                configuration.GetConnectionString("Notifications")
                ?? throw new InvalidOperationException("Missing connection string 'Notifications'.")));

        services.TryAddSingleton(TimeProvider.System);

        // Scoped: the dispatcher resolves handlers inside a scope per batch.
        services.AddScoped<IIntegrationEventHandler<SeatSoldV1>, SeatSoldNotifier>();

        if (configuration.GetValue<bool>("Notifications:MigrateOnStartup"))
        {
            services.AddModuleMigrator<NotificationsDbContext>("Notifications");
        }

        return services;
    }
}
