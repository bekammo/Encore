using Encore.Modules.Payments.Contracts;

namespace Encore.Modules.Payments.UnitTests;

/// <summary>
/// The strings client and server must spell identically. The server's route prefix is a
/// literal (so the OpenAPI drift test can read it), so this ties it to the client's constant.
/// </summary>
public class PaymentsServiceApiTests
{
    /// <summary>The mounted prefix matches the constant, or every service call 404s.</summary>
    [Fact]
    public void ThePrefixShouldBeWhatTheEndpointsMountOn() =>
        Assert.Equal("/internal/payments", PaymentsServiceApi.Prefix);

    /// <summary>The header name matches, or every call is a confusing 401.</summary>
    [Fact]
    public void TheServiceTokenHeaderShouldBeWhatTheFilterReads() =>
        Assert.Equal("X-Service-Token", PaymentsServiceApi.ServiceTokenHeader);
}
