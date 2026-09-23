using System.Net;
using System.Text;
using Encore.Modules.Orders.Data;
using Encore.Modules.Payments.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>
/// The HTTP adapter to the Payments service, against a stub on a real loopback socket, so
/// refused connections and unanswered requests are real. <c>PaymentServiceEndpointsTests</c>
/// pins the other end of the wire format.
/// </summary>
public sealed class HttpOrderPaymentsTests : IAsyncLifetime
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PaymentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private WebApplication _service = null!;
    private string _baseAddress = null!;

    /// <summary>What the stub answers with next, set per test.</summary>
    private (int Status, string Body) _next = (StatusCodes.Status200OK, "{}");

    /// <summary>How long the stub sits on a request before answering.</summary>
    private TimeSpan _delay = TimeSpan.Zero;

    /// <summary>Every path the stub was asked for, in order. One instance per test.</summary>
    private readonly List<string> _paths = [];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _service = builder.Build();

        // One handler for all three routes; tests that care check which path was asked for.
        _service.Map("/internal/payments/{operation}", async (HttpContext http) =>
        {
            _paths.Add(http.Request.Path);

            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay).ConfigureAwait(false);
            }

            http.Response.StatusCode = _next.Status;
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(_next.Body).ConfigureAwait(false);
        });

        await _service.StartAsync();

        var address = _service.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        _baseAddress = address.TrimEnd('/') + "/internal/payments/";
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _service.DisposeAsync();

    // -- the mapping table ------------------------------------------------

    [Theory]
    [InlineData(200, "authorized", AuthorizePaymentStatus.Authorized)]
    [InlineData(200, "already_captured", AuthorizePaymentStatus.AlreadyCaptured)]
    [InlineData(402, "declined", AuthorizePaymentStatus.Declined)]
    [InlineData(504, "timed_out", AuthorizePaymentStatus.TimedOut)]
    public async Task Authorize_ShouldMapEveryAnswerThatNamesAnAttempt(
        int status,
        string reason,
        AuthorizePaymentStatus expected)
    {
        Answer(status, reason, PaymentId);

        var response = await Payments().AuthorizeAsync(
            new AuthorizePaymentRequest(OrderId, ClientId, 10m, "GBP"));

        Assert.Equal(expected, response.Status);
        Assert.Equal(PaymentId, response.PaymentId);
    }

    /// <summary>The one authorize answer that names no attempt.</summary>
    [Fact]
    public async Task Authorize_WhenAnotherAttemptIsInFlight_ShouldCarryNoPaymentId()
    {
        Answer(StatusCodes.Status409Conflict, "concurrent_attempt_in_flight", paymentId: null);

        var response = await Payments().AuthorizeAsync(
            new AuthorizePaymentRequest(OrderId, ClientId, 10m, "GBP"));

        Assert.Equal(AuthorizePaymentStatus.ConcurrentAttemptInFlight, response.Status);
        Assert.Null(response.PaymentId);
    }

    [Theory]
    [InlineData(200, "captured", CapturePaymentStatus.Captured)]
    [InlineData(504, "timed_out", CapturePaymentStatus.TimedOut)]
    public async Task Capture_ShouldMapEveryAnswerThatNamesAnAttempt(
        int status,
        string reason,
        CapturePaymentStatus expected)
    {
        Answer(status, reason, PaymentId);

        var response = await Payments().CaptureAsync(new CapturePaymentRequest(OrderId, ClientId));

        Assert.Equal(expected, response.Status);
        Assert.Equal(PaymentId, response.PaymentId);
    }

    [Fact]
    public async Task Capture_WithNothingHeld_ShouldSayThereIsNoAuthorization()
    {
        Answer(StatusCodes.Status409Conflict, "no_authorization", paymentId: null);

        var response = await Payments().CaptureAsync(new CapturePaymentRequest(OrderId, ClientId));

        Assert.Equal(CapturePaymentStatus.NoAuthorization, response.Status);
    }

    [Theory]
    [InlineData(200, "voided", VoidPaymentStatus.Voided)]
    [InlineData(409, "already_captured", VoidPaymentStatus.AlreadyCaptured)]
    [InlineData(504, "timed_out", VoidPaymentStatus.TimedOut)]
    public async Task Void_ShouldMapEveryAnswerThatNamesAnAttempt(
        int status,
        string reason,
        VoidPaymentStatus expected)
    {
        Answer(status, reason, PaymentId);

        var response = await Payments().VoidAsync(new VoidPaymentRequest(OrderId, ClientId));

        Assert.Equal(expected, response.Status);
        Assert.Equal(PaymentId, response.PaymentId);
    }

    [Fact]
    public async Task Void_WithNothingHeld_ShouldSayThereIsNoAuthorization()
    {
        Answer(StatusCodes.Status409Conflict, "no_authorization", paymentId: null);

        var response = await Payments().VoidAsync(new VoidPaymentRequest(OrderId, ClientId));

        Assert.Equal(VoidPaymentStatus.NoAuthorization, response.Status);
    }

    // -- when the far side does not answer --------------------------------

    /// <summary>
    /// Anything unreadable is a timeout: the one status already handled safely for "the money
    /// may or may not be held".
    /// </summary>
    [Theory]
    [InlineData(500, "{}")]
    [InlineData(502, "<html>upstream is angry</html>")]
    [InlineData(200, "not json at all")]
    [InlineData(200, "{\"outcome\":\"a_word_this_version_does_not_know\"}")]
    public async Task Authorize_WhenTheAnswerCannotBeRead_ShouldReportATimeout(int status, string body)
    {
        _next = (status, body);

        var response = await Payments().AuthorizeAsync(
            new AuthorizePaymentRequest(OrderId, ClientId, 10m, "GBP"));

        Assert.Equal(AuthorizePaymentStatus.TimedOut, response.Status);
    }

    /// <summary>A service that never answers is a timeout, not an exception.</summary>
    [Fact]
    public async Task Authorize_WhenTheServiceNeverAnswers_ShouldReportATimeout()
    {
        _delay = TimeSpan.FromSeconds(5);

        var response = await Payments(timeout: TimeSpan.FromMilliseconds(250)).AuthorizeAsync(
            new AuthorizePaymentRequest(OrderId, ClientId, 10m, "GBP"));

        Assert.Equal(AuthorizePaymentStatus.TimedOut, response.Status);
    }

    /// <summary>A service that is not there at all is also a timeout.</summary>
    [Fact]
    public async Task Authorize_WhenNothingIsListening_ShouldReportATimeout()
    {
        // Port 1 on loopback: reserved, never bound, refuses immediately.
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1/internal/payments/") };

        var response = await new HttpOrderPayments(client).AuthorizeAsync(
            new AuthorizePaymentRequest(OrderId, ClientId, 10m, "GBP"));

        Assert.Equal(AuthorizePaymentStatus.TimedOut, response.Status);
    }

    /// <summary>A rejected token is configuration, not a payment outcome, so it throws.</summary>
    [Fact]
    public async Task Authorize_WhenTheTokenIsRejected_ShouldThrowRatherThanReportATimeout()
    {
        Answer(StatusCodes.Status401Unauthorized, "service_token_invalid", paymentId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Payments().AuthorizeAsync(new AuthorizePaymentRequest(OrderId, ClientId, 10m, "GBP")));
    }

    /// <summary>A success that names no attempt is a wire-format bug.</summary>
    [Fact]
    public async Task Authorize_WhenASuccessNamesNoAttempt_ShouldThrow()
    {
        _next = (StatusCodes.Status200OK, "{\"outcome\":\"authorized\"}");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Payments().AuthorizeAsync(new AuthorizePaymentRequest(OrderId, ClientId, 10m, "GBP")));
    }

    // -- the request the adapter sends ------------------------------------

    /// <summary>The path is the operation.</summary>
    [Fact]
    public async Task EachOperationShouldPostToItsOwnRoute()
    {
        Answer(StatusCodes.Status200OK, "authorized", PaymentId);
        await Payments().AuthorizeAsync(new AuthorizePaymentRequest(OrderId, ClientId, 10m, "GBP"));

        Answer(StatusCodes.Status200OK, "captured", PaymentId);
        await Payments().CaptureAsync(new CapturePaymentRequest(OrderId, ClientId));

        Answer(StatusCodes.Status200OK, "voided", PaymentId);
        await Payments().VoidAsync(new VoidPaymentRequest(OrderId, ClientId));

        Assert.Equal(
            ["/internal/payments/authorize", "/internal/payments/capture", "/internal/payments/void"],
            _paths);
    }

    // -- helpers ----------------------------------------------------------

    /// <summary>
    /// Sets the next answer: a success body with <c>outcome</c> for 2xx, otherwise problem+json
    /// with <c>reason</c>, as the real endpoints emit.
    /// </summary>
    private void Answer(int status, string reason, Guid? paymentId)
    {
        var key = status is >= 200 and < 300 ? "outcome" : "reason";

        var body = new StringBuilder("{\"").Append(key).Append("\":\"").Append(reason).Append('"');

        if (paymentId is { } id)
        {
            body.Append(",\"paymentId\":\"").Append(id).Append('"');
        }

        _next = (status, body.Append('}').ToString());
    }

    private HttpOrderPayments Payments(TimeSpan? timeout = null)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(_baseAddress),
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };

        return new HttpOrderPayments(client);
    }
}
