using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// A claimed identity standing in for an Identity module, not authentication. A filter, not a
/// bound parameter, so a bad header gets a readable 400. Copied, not shared (017):
/// <c>Encore.Shared</c> must stay free of ASP.NET Core.
/// </summary>
internal sealed class ClientIdEndpointFilter : IEndpointFilter
{
    internal const string HeaderName = "X-Client-Id";

    // Different in each copy, so two filters on one route cannot collide (017).
    private const string ItemKey = "Encore.Payments.ClientId";

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

        return await next(context).ConfigureAwait(false);
    }

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
