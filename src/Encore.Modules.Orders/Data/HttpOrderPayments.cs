using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Encore.Modules.Payments.Contracts;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// <see cref="IOrderPayments"/> over HTTP, used when Payments runs as its own service.
/// Refusals are read from <c>reason</c>, never from the status code, and an unreadable
/// answer is treated as a timeout.
/// </summary>
internal sealed class HttpOrderPayments(HttpClient client) : IOrderPayments
{
    private readonly HttpClient _client = client;

    /// <inheritdoc />
    public async Task<AuthorizePaymentResponse> AuthorizeAsync(
        AuthorizePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await SendAsync(
            "authorize",
            new
            {
                orderId = request.OrderId,
                clientId = request.ClientId,
                amount = request.Amount,
                currency = request.Currency
            },
            cancellationToken).ConfigureAwait(false);

        return outcome.Reason switch
        {
            "authorized" => AuthorizePaymentResponse.Authorized(outcome.RequirePaymentId()),
            "already_captured" => AuthorizePaymentResponse.AlreadyCaptured(outcome.RequirePaymentId()),
            "declined" => AuthorizePaymentResponse.Declined(outcome.RequirePaymentId()),
            "timed_out" => AuthorizePaymentResponse.TimedOut(outcome.RequirePaymentId()),
            "concurrent_attempt_in_flight" => AuthorizePaymentResponse.ConcurrentAttemptInFlight,

            // Unreadable or missing: a timeout is the one status already handled safely
            // for "the money may or may not be held". The id is unknown, hence Guid.Empty.
            _ => AuthorizePaymentResponse.TimedOut(outcome.PaymentId ?? Guid.Empty)
        };
    }

    /// <inheritdoc />
    public async Task<CapturePaymentResponse> CaptureAsync(
        CapturePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await SendAsync(
            "capture",
            new { orderId = request.OrderId, clientId = request.ClientId },
            cancellationToken).ConfigureAwait(false);

        return outcome.Reason switch
        {
            "captured" => CapturePaymentResponse.Captured(outcome.RequirePaymentId()),
            "no_authorization" => CapturePaymentResponse.NoAuthorization,

            // Unknown is a timeout: the order becomes AwaitingCapture and the next confirm resolves it.
            _ => CapturePaymentResponse.TimedOut(outcome.PaymentId ?? Guid.Empty)
        };
    }

    /// <inheritdoc />
    public async Task<VoidPaymentResponse> VoidAsync(
        VoidPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await SendAsync(
            "void",
            new { orderId = request.OrderId, clientId = request.ClientId },
            cancellationToken).ConfigureAwait(false);

        return outcome.Reason switch
        {
            "voided" => VoidPaymentResponse.Voided(outcome.RequirePaymentId()),
            "no_authorization" => VoidPaymentResponse.NoAuthorization,
            "already_captured" => VoidPaymentResponse.AlreadyCaptured(outcome.RequirePaymentId()),
            _ => VoidPaymentResponse.TimedOut(outcome.PaymentId ?? Guid.Empty)
        };
    }

    /// <summary>
    /// One round trip, reduced to the outcome and the attempt id. Network faults become
    /// "no answer" instead of a 500; cancellation by the caller is not swallowed.
    /// </summary>
    private async Task<Outcome> SendAsync(
        string operation,
        object body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client
                .PostAsJsonAsync(operation, body, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                // Misconfiguration, not a payment outcome: fail loudly.
                throw new InvalidOperationException(
                    "The Payments service rejected this service token. Check Orders:Payments:ServiceToken.");
            }

            var document = await response.Content
                .ReadFromJsonAsync<JsonElement>(cancellationToken)
                .ConfigureAwait(false);

            return Outcome.From(document);
        }
        catch (HttpRequestException)
        {
            return Outcome.NoAnswer;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Outcome.NoAnswer;
        }
        catch (JsonException)
        {
            return Outcome.NoAnswer;
        }
    }

    /// <summary>
    /// <summary>The outcome or refusal reason, and the attempt it is about.</summary>
    private readonly record struct Outcome(string? Reason, Guid? PaymentId)
    {
        /// <summary>Nothing readable came back.</summary>
        internal static Outcome NoAnswer { get; } = new(null, null);

        /// <summary>
        /// Reads either shape: a success body carries <c>outcome</c>, a problem+json
        /// refusal carries <c>reason</c>, and both may carry <c>paymentId</c>.
        /// </summary>
        internal static Outcome From(JsonElement document)
        {
            if (document.ValueKind is not JsonValueKind.Object)
            {
                return NoAnswer;
            }

            var reason =
                document.TryGetProperty("outcome", out var outcome) && outcome.ValueKind is JsonValueKind.String
                    ? outcome.GetString()
                    : document.TryGetProperty("reason", out var refused) && refused.ValueKind is JsonValueKind.String
                        ? refused.GetString()
                        : null;

            Guid? paymentId =
                document.TryGetProperty("paymentId", out var id)
                && id.ValueKind is JsonValueKind.String
                && Guid.TryParse(id.GetString(), out var parsed)
                    ? parsed
                    : null;

            return new Outcome(reason, paymentId);
        }

        /// <summary>
        /// The attempt id for an outcome that must name one. Missing means the two sides
        /// disagree about the wire format, which is a bug.
        /// </summary>
        internal Guid RequirePaymentId() =>
            PaymentId ?? throw new InvalidOperationException(
                $"The Payments service answered '{Reason}' without naming the attempt.");
    }
}
