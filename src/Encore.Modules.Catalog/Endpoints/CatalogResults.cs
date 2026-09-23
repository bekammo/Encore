using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Catalog.Endpoints;

/// <summary>
/// The problem-details shapes this module returns: 404 for a missing addressed resource,
/// 409 for a refusal about the world, 400 for malformed input, each with a <c>reason</c>.
/// </summary>
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
    /// Well formed, but something it refers to makes it impossible, such as an event
    /// naming a venue that does not exist.
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
    /// The request itself is malformed.
    /// </summary>
    public static IResult Invalid(PathString path, string title, string detail, string reason) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: title,
            instance: path,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
