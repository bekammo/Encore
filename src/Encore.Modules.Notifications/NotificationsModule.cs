using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Notifications.Data;
using Encore.Modules.Shared.Persistence;
using Encore.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Encore.Modules.Notifications;

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

        services.AddScoped<IIntegrationEventHandler<SeatSoldV1>, SeatSoldNotifier>();

        if (configuration.GetValue<bool>("Notifications:MigrateOnStartup"))
        {
            services.AddModuleMigrator<NotificationsDbContext>("Notifications");
        }

        return services;
    }
}
