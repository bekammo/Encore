namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// The strings both sides of the Payments service API must spell identically. Addresses and
/// tokens are configuration and do not belong here.
/// </summary>
public static class PaymentsServiceApi
{
    /// <summary>The header carrying the shared service token.</summary>
    public const string ServiceTokenHeader = "X-Service-Token";

    /// <summary>The path prefix every service route sits under.</summary>
    public const string Prefix = "/internal/payments";
}
