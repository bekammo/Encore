using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Orders.Endpoints;

/// <summary>
/// Reads and validates the <c>X-Client-Id</c> header for every route that acts
/// on behalf of a client.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a copy of Inventory's filter, and copying it was the decision.</b>
/// Promoting it to <c>Encore.Shared</c> would have been the obvious move and is
/// the wrong one twice over: Shared holds zero packages on purpose, because
/// <c>Inventory.Domain</c> references it and must stay free of infrastructure,
/// and an <see cref="IEndpointFilter"/> would drag
/// <c>Microsoft.AspNetCore.App</c> in through that door. 019 makes the second
/// argument — Shared is for contracts every module agrees on, not a drawer for
/// whatever two modules happen to share. Forty lines duplicated is cheaper than
/// either. See <c>DECISIONS.md</c> 024.
/// </para>
/// <para>
/// <b>The item key differs from Inventory's, and that matters.</b> Both filters
/// run in one host against one <see cref="HttpContext"/>. A shared key would work
/// perfectly until the day a route carried both filters, and then it would work
/// by accident.
/// </para>
/// <para>
/// <b>This is not authentication.</b> Anyone can send any client id. It is a
/// deliberate stand-in for the Identity module that does not exist yet, which is
/// also why nothing here returns 401 or 403 — there is nothing authorising.
/// </para>
/// <para>
/// A filter on the route group rather than a bound parameter, for 014's two
/// reasons: a failed parameter bind produces a framework 400 with an empty body,
/// which is the response nobody can diagnose from a load harness; and a group
/// filter cannot be forgotten by the next endpoint added.
/// </para>
/// </remarks>
internal sealed class ClientIdEndpointFilter : IEndpointFilter
{
    /// <summary>The header carrying the caller's claimed identity.</summary>
    internal const string HeaderName = "X-Client-Id";

    private const string ItemKey = "Encore.Orders.ClientId";

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
    /// nothing stored means the filter was not applied, which is a wiring bug and
    /// throws rather than inventing an identity.
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
