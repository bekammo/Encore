using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>The problem-details shapes this module's read routes return.</summary>
internal static class PaymentResults
{
    /// <summary>
    /// The payment is not there, or is not this client's: the same answer, so ids cannot be probed.
    /// </summary>
    public static IResult NotFound(PathString path) =>
        TypedResults.Problem(
            detail: "No such payment.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Payment not found",
            instance: path,
            extensions: new Dictionary<string, object?> { ["reason"] = "payment_not_found" });
}
