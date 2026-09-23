using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// Reads and validates the <c>X-Client-Id</c> header, a claimed identity rather than
/// authentication. Copied rather than shared: <c>Encore.Shared</c> must stay free of
/// ASP.NET Core. Its item key differs so two filters on one route cannot collide.
/// </summary>
internal sealed class ClientIdEndpointFilter : IEndpointFilter
{
    internal const string HeaderName = "X-Client-Id";

    private const string ItemKey = "Encore.Payments.ClientId";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers[HeaderName];

        if (header.Count is 0)
        {
            return Problem(context, $"The {HeaderName} header is required.");
        }

        if (header.Count > 1)
        {
            return Problem(context, $"The {HeaderName} header must be sent exactly once.");
        }

        if (!Guid.TryParse(header[0], out var clientId) || clientId == Guid.Empty)
        {
            return Problem(context, $"The {HeaderName} header must be a non-empty GUID.");
        }

        context.HttpContext.Items[ItemKey] = clientId;

        return await next(context);
    }

    /// <summary>
    /// The client id for this request. Throws if the filter was not applied to the route.
    /// </summary>
    internal static Guid ClientId(HttpContext context) =>
        context.Items[ItemKey] is Guid clientId
            ? clientId
            : throw new InvalidOperationException(
                $"No client id on this request. Is {nameof(ClientIdEndpointFilter)} applied to this route?");

    private static IResult Problem(EndpointFilterInvocationContext context, string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid client identity",
            instance: context.HttpContext.Request.Path);
}
