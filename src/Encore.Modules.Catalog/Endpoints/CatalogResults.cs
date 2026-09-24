using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Catalog.Endpoints;

internal static class CatalogResults
{
    public static IResult NotFound(PathString path, string title, string detail, string reason) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status404NotFound,
            title: title,
            instance: path,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });

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

    public static IResult Invalid(PathString path, string title, string detail, string reason) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: title,
            instance: path,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
