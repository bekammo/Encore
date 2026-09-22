using Encore.Modules.Payments.Contracts;

namespace Encore.Modules.Payments.UnitTests;

/// <summary>
/// The two strings both sides of the process boundary have to spell identically.
/// DECISIONS 061.
/// </summary>
/// <remarks>
/// <para>
/// <c>PaymentServiceEndpoints</c> writes the prefix as a literal because DECISIONS
/// 059's route reader resolves a <c>MapGroup</c> by reading the string out of the
/// source, and cannot follow a constant. That leaves two spellings of one path — the
/// literal the server mounts on, and the constant the client is told about — and
/// nothing tying them together. This is that tie.
/// </para>
/// <para>
/// It is a unit test rather than an integration one on purpose: the failure it
/// guards against is a rename, and a rename is visible without a database.
/// </para>
/// </remarks>
public class PaymentsServiceApiTests
{
    /// <summary>
    /// If this fails, the group in <c>PaymentServiceEndpoints.MapPaymentServiceEndpoints</c>
    /// and this constant have drifted, and every service call 404s.
    /// </summary>
    [Fact]
    public void ThePrefixShouldBeWhatTheEndpointsMountOn() =>
        Assert.Equal("/internal/payments", PaymentsServiceApi.Prefix);

    /// <summary>
    /// The header name, pinned for the same reason: a rename on one side is a 401
    /// that looks like a bad token.
    /// </summary>
    [Fact]
    public void TheServiceTokenHeaderShouldBeWhatTheFilterReads() =>
        Assert.Equal("X-Service-Token", PaymentsServiceApi.ServiceTokenHeader);
}
