using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// Refuses any call that cannot present the shared service token, compared in fixed time.
/// A floor until Identity or mTLS exists; the routes would not change.
/// </summary>
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

        // One refusal for every way of failing, so a caller learns nothing about which.
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
    /// Compares without leaking how much of the token matched through timing.
    /// </summary>
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
