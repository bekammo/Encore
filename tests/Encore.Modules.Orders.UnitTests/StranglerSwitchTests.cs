using Encore.Modules.Orders;
using Encore.Modules.Orders.Data;
using Encore.Modules.Payments;
using Encore.Modules.Payments.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Orders.UnitTests;

/// <summary>
/// Which <see cref="IOrderPayments"/> a composed container resolves, in both registration
/// orders, with the HTTP switch set and unset. Testing each side of the seam alone missed a
/// bug where the switch silently did nothing; only composing both modules shows it.
/// </summary>
public class StranglerSwitchTests
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

    /// <summary>With no base address, the monolith is unchanged.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenTheSwitchIsUnset_TheInProcessAdapterShouldWin(bool ordersFirst)
    {
        using var provider = Compose(strangled: false, ordersFirst: ordersFirst);

        Assert.Equal("InProcessOrderPayments", Resolve(provider).GetType().Name);
    }

    /// <summary>
    /// The resolved adapter, compared by type name, since <c>InProcessOrderPayments</c> is
    /// internal to Payments.
    /// </summary>
    private static IOrderPayments Resolve(ServiceProvider provider)
    {
        // A scope, because both adapters are scoped.
        using var scope = provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IOrderPayments>();
    }

    private static ServiceProvider Compose(bool strangled, bool ordersFirst)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Orders"] = "Host=nowhere;Database=encore;Username=encore;Password=encore",
            ["ConnectionStrings:Payments"] = "Host=nowhere;Database=encore;Username=encore;Password=encore",
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
