using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Catalog.Endpoints;

/// <summary>
/// The <c>ProblemDetails</c> shapes this module returns, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not the twin of <c>SeatResults</c>, and it should not grow into
/// one.</b> That class exists because Inventory's use cases return closed
/// outcome enums and something has to decide, exhaustively, which status each
/// one deserves. Catalog has no use-case layer and no outcome enum — it is CRUD
/// over uncontended tables — so there is nothing to switch on. What is left is
/// the part still worth centralising: the <c>reason</c> / <c>retriable</c> /
/// <c>instance</c> shape, so it is not retyped slightly differently at each of
/// a dozen call sites.
/// </para>
/// <para>
/// The status rule is 014's, unchanged. A refusal about the state of the world
/// is 409; only the addressed resource being absent is 404; malformed input is
/// 400. There is no 401 or 403 here, because there is nothing authenticating
/// and nothing authorising.
/// </para>
/// </remarks>
internal static class CatalogResults
{
    /// <summary>The thing named in the URL does not exist.</summary>
    public static IResult NotFound(PathString path, string title, string detail, string reason) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status404NotFound,
            title: title,
            instance: path,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });

    /// <summary>
    /// The request is well formed, but something it refers to makes it
    /// impossible right now — an event naming a venue that does not exist, for
    /// instance. 409 rather than 404 because the resource being addressed
    /// (<c>/catalog/events</c>) is perfectly real; it is the world the body
    /// describes that is not.
    /// </summary>
    public static IResult Conflict(PathString path, string reason, string detail, bool retriable) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            title: "Request cannot be satisfied",
            instance: path,
            extensions: new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["retriable"] = retriable
            });

    /// <summary>
    /// The request itself is malformed. Carries a <c>reason</c> like the other
    /// two: 014's principle is that the status names the class of failure and
    /// the reason names which one, and that is as useful at 400 as at 409.
    /// </summary>
    public static IResult Invalid(PathString path, string title, string detail, string reason) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: title,
            instance: path,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
