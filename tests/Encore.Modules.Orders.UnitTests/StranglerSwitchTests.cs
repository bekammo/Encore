using Encore.Modules.Orders;
using Encore.Modules.Orders.Data;
using Encore.Modules.Payments;
using Encore.Modules.Payments.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Orders.UnitTests;

/// <summary>
/// Which <see cref="IOrderPayments"/> a composed host actually resolves, in both
/// registration orders and with the switch both set and unset.
/// </summary>
/// <remarks>
/// <para>
/// <b>061 asserted this in a comment and the comment was wrong.</b> It argued that
/// <c>ServiceCollectionDescriptorExtensions.Replace</c> made the outcome
/// independent of whether Orders or Payments was registered first. Replace removes
/// the <i>first existing</i> registration and appends its own — so with Orders
/// registered before Payments, which is the order in <c>Encore.Api</c>, there was
/// nothing to remove and the in-process adapter was appended afterwards. Last-wins
/// then handed every payment to the in-process adapter and the strangled
/// configuration was a monolith wearing two containers.
/// </para>
/// <para>
/// <b>Nothing could have caught it.</b> <c>HttpOrderPaymentsTests</c> constructs the
/// adapter directly and <c>PaymentServiceEndpointsTests</c> drives the service's
/// routes; both were green and both would have stayed green. The bug lived in the
/// one place neither looks — the composition — and it took stopping the Payments
/// service under load and watching the confirms keep succeeding (063). These four
/// tests are the cheap version of that discovery.
/// </para>
/// <para>
/// No database and no network: nothing is resolved here that opens a connection,
/// and a <c>DbContext</c> is perfectly constructible against a connection string
/// pointing at nothing. What is under test is the container's answer, not what the
/// answer would then do.
/// </para>
/// </remarks>
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

    /// <summary>
    /// With no base address the monolith is unchanged, which is the half of 061's
    /// claim that was always true and must stay true.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenTheSwitchIsUnset_TheInProcessAdapterShouldWin(bool ordersFirst)
    {
        using var provider = Compose(strangled: false, ordersFirst: ordersFirst);

        Assert.Equal("InProcessOrderPayments", Resolve(provider).GetType().Name);
    }

    /// <summary>
    /// The resolved adapter, compared by type name rather than by type.
    /// </summary>
    /// <remarks>
    /// <c>InProcessOrderPayments</c> is internal to Payments and this suite is not
    /// one of its friends. Widening an assembly's internals so that another
    /// module's tests can name a class would be a worse trade than comparing a
    /// string: the question here is only which of two registrations survived, and
    /// the name answers it exactly.
    /// </remarks>
    private static IOrderPayments Resolve(ServiceProvider provider)
    {
        // A scope, because both adapters are scoped. Resolving from the root
        // provider would throw before the test could assert anything.
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
