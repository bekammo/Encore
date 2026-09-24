using Encore.Modules.Orders;
using Encore.Modules.Orders.Data;
using Encore.Modules.Payments;
using Encore.Modules.Payments.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Orders.UnitTests;

/// <summary>
/// Testing each side of the seam alone missed a switch that silently did nothing; only
/// composing both modules shows it (018).
/// </summary>
public sealed class StranglerSwitchTests
{
    private const string BaseAddress = "http://payments-api:8080/internal/payments";

    [Fact]
    public void WhenOrdersIsRegisteredFirst_TheHttpAdapterShouldWin()
    {
        using var provider = Compose(strangled: true, ordersFirst: true);

        Assert.Equal(nameof(HttpOrderPayments), Resolve(provider).GetType().Name);
    }

    [Fact]
    public void WhenPaymentsIsRegisteredFirst_TheHttpAdapterShouldWin()
    {
        using var provider = Compose(strangled: true, ordersFirst: false);

        Assert.Equal(nameof(HttpOrderPayments), Resolve(provider).GetType().Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenTheSwitchIsUnset_TheInProcessAdapterShouldWin(bool ordersFirst)
    {
        using var provider = Compose(strangled: false, ordersFirst: ordersFirst);

        // By name: InProcessOrderPayments is internal to Payments.
        Assert.Equal("InProcessOrderPayments", Resolve(provider).GetType().Name);
    }

    private static IOrderPayments Resolve(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IOrderPayments>();
    }

    private static ServiceProvider Compose(bool strangled, bool ordersFirst)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Orders"] = "Host=nowhere;Database=encore;Username=encore;Password=encore",
            ["ConnectionStrings:Payments"] = "Host=nowhere;Database=encore;Username=encore;Password=encore"
        };

        if (strangled)
        {
            settings["Orders:Payments:BaseAddress"] = BaseAddress;
            settings["Orders:Payments:ServiceToken"] = "a-token";
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();

        if (ordersFirst)
        {
            services.AddOrdersModule(configuration);
            services.AddPaymentsModule(configuration);
        }
        else
        {
            services.AddPaymentsModule(configuration);
            services.AddOrdersModule(configuration);
        }

        return services.BuildServiceProvider();
    }
}
