using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// Reads and validates the <c>X-Client-Id</c> header for every route that acts on
/// behalf of a client.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third copy of this file, and the argument for copying has not changed.</b>
/// <c>Encore.Shared</c> holds zero packages on purpose, because
/// <c>Inventory.Domain</c> references it and must stay free of infrastructure; an
/// <see cref="IEndpointFilter"/> would drag <c>Microsoft.AspNetCore.App</c> in
/// through that door. Since 036 the build refuses it outright — <c>ENCORE002</c>
/// on <c>Encore.Shared</c> for the framework reference, and <c>ENCORE003</c> on
/// <c>Inventory.Domain</c> for what it drags along — which makes 024's argument
/// something the compiler now agrees with rather than something this comment has
/// to be trusted about. See <c>DECISIONS.md</c> 024 and 036.
/// </para>
/// <para>
/// A third copy is worth naming as a cost rather than shrugged at: forty lines is
/// cheap, a hundred and twenty is where somebody starts wondering. The exit, if
/// there is ever a fourth, is a small web-only shared assembly that
/// <c>Inventory.Domain</c> does not reference — not a drawer in Shared.
/// </para>
/// <para>
/// <b>The item key differs from the other two, and that matters.</b> All three
/// filters run in one host against one <see cref="HttpContext"/>. A shared key
/// would work perfectly until the day a route carried two of them, and then it
/// would work by accident.
/// </para>
/// <para>
/// <b>This is not authentication.</b> Anyone can send any client id. It is a
/// deliberate stand-in for the Identity module that does not exist yet, which is
/// also why nothing here returns 401 or 403 — there is nothing authorising.
/// </para>
/// </remarks>
internal sealed class ClientIdEndpointFilter : IEndpointFilter
{
    /// <summary>The header carrying the caller's claimed identity.</summary>
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
