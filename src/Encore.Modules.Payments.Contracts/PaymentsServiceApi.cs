namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// The few strings a caller needs to reach Payments over HTTP rather than through
/// the container. DECISIONS 061.
/// </summary>
/// <remarks>
/// <para>
/// These belong on the public face for the same reason <see cref="IOrderPayments"/>
/// does: they are the shape of the seam, and a caller takes a dependency on this
/// assembly and nothing else. The alternative is the header name written as a
/// literal on both sides of a process boundary, which is a thing that drifts and
/// fails as a 401 nobody can explain.
/// </para>
/// <para>
/// No base address and no token here — those are deployment facts and belong in
/// configuration. What is here is only what both sides must spell identically.
/// </para>
/// </remarks>
public static class PaymentsServiceApi
{
    /// <summary>The header carrying the shared service token.</summary>
    public const string ServiceTokenHeader = "X-Service-Token";

    /// <summary>The path prefix every service route sits under.</summary>
    public const string Prefix = "/internal/payments";
}
