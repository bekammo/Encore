using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Inventory.Endpoints;

/// <summary>
/// Reads and validates the <c>X-Client-Id</c> header for every route that acts
/// on behalf of a client.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not authentication.</b> Anyone can send any client id, and
/// changing the header resets their hold cap. It is a deliberate stand-in for
/// the Identity module that does not exist yet, so that the seat use cases can
/// be exercised end to end without inventing an auth story first. Nothing here
/// returns 401 or 403, because there is nothing doing the authorising — every
/// refusal on these routes is about the state of a seat, not about permission.
/// </para>
/// <para>
/// A filter on the route group rather than a bound parameter, for two reasons.
/// A failed parameter bind produces a framework 400 with an empty body, which
/// is exactly the response nobody can diagnose from a load harness at 3am; and
/// a group filter cannot be forgotten by the next endpoint added, where a
/// parameter can simply be left off.
/// </para>
/// </remarks>
internal sealed class ClientIdEndpointFilter : IEndpointFilter
{
    /// <summary>The header carrying the caller's claimed identity.</summary>
    internal const string HeaderName = "X-Client-Id";

    private const string ItemKey = "Encore.Inventory.ClientId";

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
    /// The client id for this request.
    /// </summary>
    /// <remarks>
    /// Only valid on routes carrying <see cref="ClientIdEndpointFilter"/>; the
    /// filter has already rejected the request otherwise, so reaching this with
    /// nothing stored means the filter was not applied, which is a wiring bug
    /// and throws rather than inventing an identity.
    /// </remarks>
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
