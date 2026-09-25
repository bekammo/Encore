using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Shared.Http;

/// <summary>
/// A claimed identity standing in for an Identity module, not authentication. A filter, not a
/// bound parameter, so a bad header gets a readable 400.
/// </summary>
public sealed class ClientIdEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "X-Client-Id";

    // One key for every module: two copies of this filter on one route read the same header and
    // store the same value (029).
    private const string ItemKey = "Encore.ClientId";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

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

    public static Guid ClientId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items[ItemKey] is Guid clientId
            ? clientId
            : throw new InvalidOperationException(
                $"No client id on this request. Is {nameof(ClientIdEndpointFilter)} applied to this route?");
    }

    private static IResult Problem(EndpointFilterInvocationContext context, string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid client identity",
            instance: context.HttpContext.Request.Path);
}
