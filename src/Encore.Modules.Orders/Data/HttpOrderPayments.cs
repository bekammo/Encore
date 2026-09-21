using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Encore.Modules.Payments.Contracts;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// <see cref="IOrderPayments"/> over HTTP, for when Payments is its own service.
/// DECISIONS 061.
/// </summary>
/// <remarks>
/// <para>
/// The twin of <c>InProcessOrderPayments</c>, and the point of the pair: Orders
/// depends on <see cref="IOrderPayments"/> and cannot tell which one it has. Which
/// is registered is a line in <c>OrdersModule</c>, and that line is the strangler's
/// switch.
/// </para>
/// <para>
/// <b>It lives in Orders, not in Payments.</b> A consumer owns how it reaches a
/// service; Payments owns what the service does. Putting the client here keeps
/// Payments' assembly free of code that only its callers run, and means Orders still
/// names nothing of Payments but its <c>.Contracts</c> assembly.
/// </para>
/// <para>
/// <b>Every refusal is read from <c>reason</c>, never from the status code.</b>
/// Several statuses share a code — 409 covers both a lost race and a missing
/// authorisation — so the code is for proxies and humans and the string is the half
/// that maps one-to-one onto the contract's vocabulary. A body this cannot read is
/// treated as no answer at all, which is the safe reading: see below.
/// </para>
/// </remarks>
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

            // No answer, or one this cannot read. Reported as a timeout because that
            // is what it is from Orders' side, and because TimedOut is the one status
            // whose handling is already correct for "the money may or may not be
            // held": the order stays Pending, nothing is sold, and the next confirm
            // asks again under the same key. Calling it Declined would be a guess
            // that loses money; calling it Authorized would be a guess that sells
            // seats against funds nobody has.
            //
            // The attempt id is genuinely unknown here, and the contract requires
            // one, so this reuses the empty Guid to mean "no attempt this caller can
            // name". Orders never dereferences it on this path.
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

            // Same reading as authorize: unknown is a timeout. A capture that may or
            // may not have happened leaves the order AwaitingCapture, which 027 built
            // for exactly this and which the next confirm resolves.
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
    /// One round trip, reduced to the two things every caller above branches on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Network faults are caught rather than thrown. A confirm is a request with a
    /// customer waiting on it, and an unhandled <see cref="HttpRequestException"/>
    /// there is a 500 that tells them nothing and leaves the order in a state nobody
    /// chose. The callers above turn an unreadable answer into a timeout, which is a
    /// state the design already knows how to finish.
    /// </para>
    /// <para>
    /// <see cref="OperationCanceledException"/> is deliberately not caught when the
    /// caller's own token fired: that is the request being abandoned, not the
    /// service failing, and swallowing it would record an attempt nobody is waiting
    /// for. A timeout from <see cref="HttpClient"/> itself surfaces as a
    /// <see cref="TaskCanceledException"/> with an untriggered token, and that one is
    /// caught — it is the service not answering.
    /// </para>
    /// </remarks>
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
                // Misconfiguration, not a payment outcome, and it will not fix itself
                // by being retried into a timeout. Fail loudly on the first call.
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
    /// What came back, as the two fields that matter: the outcome or refusal reason,
    /// and the attempt it is about.
    /// </summary>
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
        /// The attempt id for an outcome that must name one.
        /// </summary>
        /// <remarks>
        /// A success body without a <c>paymentId</c> means the two sides disagree
        /// about the wire format, which is a bug rather than a payment state, and the
        /// contract has no member for it. Throwing names the problem where it
        /// happened instead of handing Orders an empty Guid that will be stored
        /// against a real order.
        /// </remarks>
        internal Guid RequirePaymentId() =>
            PaymentId ?? throw new InvalidOperationException(
                $"The Payments service answered '{Reason}' without naming the attempt.");
    }
}
