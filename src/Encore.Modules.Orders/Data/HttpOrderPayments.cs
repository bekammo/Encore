using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Encore.Modules.Payments.Contracts;
using Outcomes = Encore.Modules.Payments.Contracts.PaymentsServiceApi.Outcomes;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// Reads the answer from the body, <c>outcome</c> on success and <c>reason</c> on a refusal,
/// never from the status code. A missing or unreadable answer is a timeout: the one status
/// already handled safely for "the money may or may not be held".
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

        var outcome = await SendAsync("authorize", request, cancellationToken).ConfigureAwait(false);

        return outcome.Reason switch
        {
            Outcomes.Authorized => AuthorizePaymentResponse.Authorized(outcome.RequirePaymentId()),
            Outcomes.AlreadyCaptured => AuthorizePaymentResponse.AlreadyCaptured(outcome.RequirePaymentId()),
            Outcomes.Declined => AuthorizePaymentResponse.Declined(outcome.RequirePaymentId()),
            Outcomes.TimedOut => AuthorizePaymentResponse.TimedOut(outcome.RequirePaymentId()),
            Outcomes.ConcurrentAttemptInFlight => AuthorizePaymentResponse.ConcurrentAttemptInFlight,
            _ => AuthorizePaymentResponse.TimedOut(outcome.PaymentId ?? Guid.Empty)
        };
    }

    /// <inheritdoc />
    public async Task<CapturePaymentResponse> CaptureAsync(
        CapturePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await SendAsync("capture", request, cancellationToken).ConfigureAwait(false);

        return outcome.Reason switch
        {
            Outcomes.Captured => CapturePaymentResponse.Captured(outcome.RequirePaymentId()),
            Outcomes.NoAuthorization => CapturePaymentResponse.NoAuthorization,
            _ => CapturePaymentResponse.TimedOut(outcome.PaymentId ?? Guid.Empty)
        };
    }

    /// <inheritdoc />
    public async Task<VoidPaymentResponse> VoidAsync(
        VoidPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await SendAsync("void", request, cancellationToken).ConfigureAwait(false);

        return outcome.Reason switch
        {
            Outcomes.Voided => VoidPaymentResponse.Voided(outcome.RequirePaymentId()),
            Outcomes.NoAuthorization => VoidPaymentResponse.NoAuthorization,
            Outcomes.AlreadyCaptured => VoidPaymentResponse.AlreadyCaptured(outcome.RequirePaymentId()),
            _ => VoidPaymentResponse.TimedOut(outcome.PaymentId ?? Guid.Empty)
        };
    }

    private async Task<Outcome> SendAsync<TRequest>(
        string operation,
        TRequest body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client
                .PostAsJsonAsync(operation, body, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
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

    private readonly record struct Outcome(string? Reason, Guid? PaymentId)
    {
        internal static Outcome NoAnswer { get; } = new(null, null);

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

        // A known outcome without an id is a wire-format bug, not an outage, so it throws.
        internal Guid RequirePaymentId() =>
            PaymentId ?? throw new InvalidOperationException(
                $"The Payments service answered '{Reason}' without naming the attempt.");
    }
}
