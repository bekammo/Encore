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
/// <para>
/// <b>There is no <c>MapNotificationsModule</c>, and its absence is the interesting
/// thing about this module.</b> Every other module is reached by a client over HTTP;
/// this one is reached only by an event, so it has no routes to map and a mapping
/// seam would be an empty method existing to complete a pattern. It is the first
/// module whose entire inbound surface is a subscription.
/// </para>
/// <para>
/// <b>It does have a read side worth exposing, and that is deliberately not built
/// here.</b> A <c>GET /notifications</c> is private data, so by 033 it carries
/// <c>X-Client-Id</c> — which would be the fourth copy of
/// <c>ClientIdEndpointFilter</c>, and 033 named that exact number as the trigger for
/// a different answer: "a small web-only shared assembly that
/// <c>Inventory.Domain</c> does not reference — not a drawer in Shared". Writing the
/// fourth copy anyway would spend a trigger that was recorded precisely so it would
/// not be spent quietly. So the route waits for that assembly, and until then this
/// module's behaviour is proven the way Payments' write side already is: by its
/// integration tests rather than by a request anyone can send.
/// </para>
/// <para>
/// <b>The handler registration is the whole subscription.</b> Inventory's dispatcher
/// resolves <c>IIntegrationEventHandler&lt;SeatSoldV1&gt;</c> from the container and
/// finds this one; neither module names the other's implementation, and Inventory
/// has no idea anybody is listening. The day a second module wants the same event it
/// adds a line like this one and changes nothing in Inventory — which is the property
/// that makes an outbox worth the transaction it costs.
/// </para>
/// </remarks>
public static class NotificationsModule
{
    /// <summary>Registers the module's services.</summary>
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<NotificationsDbContext>(options =>
            options.UseNotificationsNpgsql(
                configuration.GetConnectionString("Notifications")
                ?? throw new InvalidOperationException("Missing connection string 'Notifications'.")));

        services.TryAddSingleton(TimeProvider.System);

        // Scoped, because the dispatcher resolves handlers inside the scope it opens
        // per batch and the context they write through is scoped too.
        services.AddScoped<IIntegrationEventHandler<SeatSoldV1>, SeatSoldNotifier>();

        if (configuration.GetValue<bool>("Notifications:MigrateOnStartup"))
        {
            services.AddModuleMigrator<NotificationsDbContext>("Notifications");
        }

        return services;
    }
}
