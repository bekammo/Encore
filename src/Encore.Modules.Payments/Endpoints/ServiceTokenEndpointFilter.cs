using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// Refuses anything that cannot present the shared service token, and is what
/// keeps DECISIONS 033 true once Payments has a write surface at all.
/// </summary>
/// <remarks>
/// <para>
/// 033 refused a <c>POST /payments</c> because a client that can charge itself has
/// walked around the order flow entirely — it could authorise money against an
/// order it does not own, or against no order at all, and this module has no
/// principled way to refuse because it does not know what a checkout is. That
/// argument is about a <i>customer</i>, and it is untouched: the routes this filter
/// guards take no <c>X-Client-Id</c>, are not mounted by
/// <c>MapPaymentsModule</c>, and a caller holding nothing but a client id cannot
/// reach them. See DECISIONS 061.
/// </para>
/// <para>
/// <b>A shared secret is the floor, not the ceiling.</b> There is no Identity
/// module yet, so this is what is available; it is compared in fixed time and it is
/// the only thing standing between the public internet and an authorise call, which
/// is why the service refuses to start without one rather than defaulting to a
/// development value. When Identity arrives, or when the deployment grows mTLS, this
/// is the seam that gets replaced — the routes do not change.
/// </para>
/// </remarks>
internal sealed class ServiceTokenEndpointFilter(string expected) : IEndpointFilter
{
    internal const string HeaderName = Contracts.PaymentsServiceApi.ServiceTokenHeader;

    private readonly string _expected = expected;

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers[HeaderName];

        // One refusal for every way of failing, and it says nothing about which way.
        // A missing token, a malformed one and a wrong one are the same 401 with the
        // same body: telling an unauthenticated caller which of those it got is
        // telling it how to get closer.
        if (header.Count is not 1 || !FixedTimeEquals(header[0], _expected))
        {
            return TypedResults.Problem(
                detail: $"The {HeaderName} header is required and must be the configured service token.",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthenticated service call",
                instance: context.HttpContext.Request.Path,
                extensions: new Dictionary<string, object?> { ["reason"] = "service_token_invalid" });
        }

        return await next(context);
    }

    /// <summary>
    /// Compares without leaking the answer through how long it took.
    /// </summary>
    /// <remarks>
    /// <c>string.Equals</c> returns on the first differing character, so the time it
    /// takes is a measure of how much of the token the caller already has. That is a
    /// slow oracle and a real one, and avoiding it costs nothing here.
    /// </remarks>
    private static bool FixedTimeEquals(string? candidate, string expected)
    {
        if (candidate is null)
        {
            return false;
        }

        var left = System.Text.Encoding.UTF8.GetBytes(candidate);
        var right = System.Text.Encoding.UTF8.GetBytes(expected);

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }
}
