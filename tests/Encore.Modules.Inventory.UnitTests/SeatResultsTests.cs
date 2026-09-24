using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Inventory.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Encore.Modules.Inventory.UnitTests;

public sealed class SeatResultsTests
{
    private static readonly Guid SeatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Expiry = new(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc);
    private static readonly PathString Path = new("/events/e/seats/s/hold");

    private static int StatusOf(IResult result) => result switch
    {
        IStatusCodeHttpResult status => status.StatusCode
            ?? throw new InvalidOperationException("Result carries no status code."),
        _ => throw new InvalidOperationException($"Unexpected result type {result.GetType().Name}.")
    };

    private static string? ReasonOf(IResult result) =>
        result is ProblemHttpResult problem
        && problem.ProblemDetails.Extensions.TryGetValue("reason", out var reason)
            ? reason as string
            : null;

    // -- Holding ----------------------------------------------------------

    [Fact]
    public void ForHold_WhenHeld_ShouldBe200WithTheExpiry()
    {
        var result = SeatResults.ForHold(SeatId, HoldSeatResult.Held(Expiry), Path);

        var ok = Assert.IsType<Ok<HeldSeatResponse>>(result);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal(SeatId, ok.Value!.SeatId);
        Assert.Equal(Expiry, ok.Value.HoldExpiresAt);
    }

    [Theory]
    [InlineData(HoldSeatOutcome.AlreadyHeld, StatusCodes.Status409Conflict, "already_held")]
    [InlineData(HoldSeatOutcome.AlreadySold, StatusCodes.Status409Conflict, "already_sold")]
    [InlineData(HoldSeatOutcome.SeatNotFound, StatusCodes.Status404NotFound, "seat_not_found")]
    [InlineData(HoldSeatOutcome.LostRace, StatusCodes.Status409Conflict, "lost_race")]
    [InlineData(HoldSeatOutcome.HoldCapReached, StatusCodes.Status409Conflict, "hold_cap_reached")]
    [InlineData(HoldSeatOutcome.ConcurrentRequestInFlight, StatusCodes.Status409Conflict, "concurrent_request_in_flight")]
    public void ForHold_Refusals_ShouldCarryStatusAndReason(
        HoldSeatOutcome outcome,
        int expectedStatus,
        string expectedReason)
    {
        var result = SeatResults.ForHold(SeatId, new HoldSeatResult(outcome), Path);

        Assert.Equal(expectedStatus, StatusOf(result));
        Assert.Equal(expectedReason, ReasonOf(result));
    }

    [Fact]
    public void ForHold_WhenCapReached_ShouldReportTheLimit()
    {
        var result = SeatResults.ForHold(SeatId, HoldSeatResult.HoldCapReached, Path);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(
            SeatReservationLimits.MaxHoldsPerClientPerEvent,
            Assert.IsType<int>(problem.ProblemDetails.Extensions["limit"]));
    }

    [Theory]
    [InlineData(HoldSeatOutcome.AlreadyHeld, true)]
    [InlineData(HoldSeatOutcome.LostRace, true)]
    [InlineData(HoldSeatOutcome.ConcurrentRequestInFlight, true)]
    [InlineData(HoldSeatOutcome.AlreadySold, false)]
    public void ForHold_ShouldSayWhetherARetryCouldWork(HoldSeatOutcome outcome, bool expected)
    {
        var result = SeatResults.ForHold(SeatId, new HoldSeatResult(outcome), Path);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(expected, problem.ProblemDetails.Extensions["retriable"]);
    }

    // -- Selling ----------------------------------------------------------

    [Fact]
    public void ForSell_WhenSold_ShouldBe200()
    {
        var result = SeatResults.ForSell(SeatId, SellSeatOutcome.Sold, Path);

        var ok = Assert.IsType<Ok<SeatActionResponse>>(result);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal("sold", ok.Value!.Status);
    }

    [Theory]
    [InlineData(SellSeatOutcome.AlreadySold, StatusCodes.Status409Conflict, "already_sold")]
    [InlineData(SellSeatOutcome.NotTheHolder, StatusCodes.Status409Conflict, "not_the_holder")]
    [InlineData(SellSeatOutcome.HoldExpired, StatusCodes.Status409Conflict, "hold_expired")]
    [InlineData(SellSeatOutcome.NoActiveHold, StatusCodes.Status409Conflict, "no_active_hold")]
    [InlineData(SellSeatOutcome.LostRace, StatusCodes.Status409Conflict, "lost_race")]
    [InlineData(SellSeatOutcome.SeatNotFound, StatusCodes.Status404NotFound, "seat_not_found")]
    public void ForSell_Refusals_ShouldCarryStatusAndReason(
        SellSeatOutcome outcome,
        int expectedStatus,
        string expectedReason)
    {
        var result = SeatResults.ForSell(SeatId, outcome, Path);

        Assert.Equal(expectedStatus, StatusOf(result));
        Assert.Equal(expectedReason, ReasonOf(result));
    }

    // -- Releasing --------------------------------------------------------

    [Fact]
    public void ForRelease_WhenReleased_ShouldBe200AndReportAvailable()
    {
        var result = SeatResults.ForRelease(SeatId, ReleaseSeatOutcome.Released, Path);

        var ok = Assert.IsType<Ok<SeatActionResponse>>(result);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal("available", ok.Value!.Status);
    }

    [Theory]
    [InlineData(ReleaseSeatOutcome.AlreadySold, StatusCodes.Status409Conflict, "already_sold")]
    [InlineData(ReleaseSeatOutcome.SoldToYou, StatusCodes.Status409Conflict, "sold_to_you")]
    [InlineData(ReleaseSeatOutcome.NotTheHolder, StatusCodes.Status409Conflict, "not_the_holder")]
    [InlineData(ReleaseSeatOutcome.LostRace, StatusCodes.Status409Conflict, "lost_race")]
    [InlineData(ReleaseSeatOutcome.SeatNotFound, StatusCodes.Status404NotFound, "seat_not_found")]
    public void ForRelease_Refusals_ShouldCarryStatusAndReason(
        ReleaseSeatOutcome outcome,
        int expectedStatus,
        string expectedReason)
    {
        var result = SeatResults.ForRelease(SeatId, outcome, Path);

        Assert.Equal(expectedStatus, StatusOf(result));
        Assert.Equal(expectedReason, ReasonOf(result));
    }

    // -- Nothing is unmapped ----------------------------------------------

    [Fact]
    public void EveryOutcome_ShouldMapToAResponse()
    {
        foreach (var outcome in Enum.GetValues<HoldSeatOutcome>())
        {
            var result = outcome is HoldSeatOutcome.Held
                ? SeatResults.ForHold(SeatId, HoldSeatResult.Held(Expiry), Path)
                : SeatResults.ForHold(SeatId, new HoldSeatResult(outcome), Path);

            Assert.InRange(StatusOf(result), 200, 499);
        }

        foreach (var outcome in Enum.GetValues<SellSeatOutcome>())
        {
            Assert.InRange(StatusOf(SeatResults.ForSell(SeatId, outcome, Path)), 200, 499);
        }

        foreach (var outcome in Enum.GetValues<ReleaseSeatOutcome>())
        {
            Assert.InRange(StatusOf(SeatResults.ForRelease(SeatId, outcome, Path)), 200, 499);
        }
    }
}
