using Encore.Modules.Payments.Contracts;

namespace Encore.Modules.Payments.UnitTests;

/// <summary>
/// Client and filter share the constant, but openapi.json spells the name again and a separately
/// deployed Payments service reads what is on the wire.
/// </summary>
public sealed class PaymentsServiceApiTests
{
    [Fact]
    public void ServiceTokenHeader_ShouldKeepItsWireName() =>
        Assert.Equal("X-Service-Token", PaymentsServiceApi.ServiceTokenHeader);
}
