using Encore.Modules.Payments.Contracts;

namespace Encore.Modules.Payments.UnitTests;

/// <summary>
/// The service token's header name. Client and filter share the constant, so this is not about
/// them agreeing: the hand-written OpenAPI document spells the name again, and a Payments
/// service deployed on its own reads what is on the wire.
/// </summary>
public class PaymentsServiceApiTests
{
    [Fact]
    public void TheServiceTokenHeaderShouldKeepItsWireName() =>
        Assert.Equal("X-Service-Token", PaymentsServiceApi.ServiceTokenHeader);
}
