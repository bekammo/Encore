using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// The one place in this module that knows a status code.
/// </summary>
/// <remarks>
/// Closer to <c>CatalogResults</c> than to <c>OrderResults</c>, and for the same
/// reason Catalog's is: this module's HTTP surface is read-only, so there are no
/// outcome enums to switch over exhaustively and nothing to map but "here it is"
/// and "it is not there". It centralises the <c>reason</c>/<c>instance</c> shape
/// 018 settled and no more than that. If a write route ever lands here, this grows
/// into the <c>OrderResults</c> shape — it should not grow into it beforehand.
/// </remarks>
internal static class PaymentResults
{
    /// <summary>
    /// The payment is not there, or is not this client's. Deliberately the same
    /// answer: distinguishing them would let anybody probe for other people's
    /// payments an id at a time, which is 011's argument applied to money.
    /// </summary>
    public static IResult NotFound(PathString path) =>
        TypedResults.Problem(
            detail: "No such payment.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Payment not found",
            instance: path,
            extensions: new Dictionary<string, object?> { ["reason"] = "payment_not_found" });
}
