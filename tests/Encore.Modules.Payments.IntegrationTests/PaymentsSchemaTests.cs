using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>The database is never emptied, so every test uses fresh order ids and keys.</summary>
public sealed class PaymentsSchemaTests(PaymentsDatabase database) : IClassFixture<PaymentsDatabase>
{
    private static readonly DateTime AttemptedAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<PaymentsDbContext> _options = database.Options;

    [Fact]
    public async Task Migrate_ShouldLeaveNoPendingMigrations()
    {
        await using var context = new PaymentsDbContext(_options);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    /// <summary>TimedOut is in the list on purpose: no answer may mean funds are held (013).</summary>
    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Captured)]
    [InlineData(PaymentStatus.TimedOut)]
    public async Task SecondLiveAttempt_ForTheSameOrder_ShouldBeRefused(PaymentStatus existing)
    {
        var orderId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        await SaveAsync(InStatus(orderId, clientId, existing));

        await using var context = new PaymentsDbContext(_options);
        context.Payments.Add(New(orderId, clientId));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Contains("ux_payments_order_live", ex.InnerException!.Message);
    }

    [Theory]
    [InlineData(PaymentStatus.Declined)]
    [InlineData(PaymentStatus.Voided)]
    public async Task SecondAttempt_AfterOneThatMovedNothing_ShouldBeAllowed(PaymentStatus existing)
    {
        var orderId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        await SaveAsync(InStatus(orderId, clientId, existing));
        await SaveAsync(New(orderId, clientId));

        await using var context = new PaymentsDbContext(_options);

        Assert.Equal(2, await context.Payments.CountAsync(payment => payment.OrderId == orderId));
    }

    [Fact]
    public async Task TwoAttempts_SharingAnIdempotencyKey_ShouldBeRefused()
    {
        var clientId = Guid.NewGuid();
        var first = New(Guid.NewGuid(), clientId);

        await SaveAsync(first);

        var second = Payment.Create(
            Guid.NewGuid(), Guid.NewGuid(), clientId, 10m, "GBP", first.IdempotencyKey, AttemptedAt);

        await using var context = new PaymentsDbContext(_options);
        context.Payments.Add(second);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Contains("ux_payments_idempotency_key", ex.InnerException!.Message);
    }

    [Fact]
    public async Task Amount_ShouldRoundTripAtFourDecimalPlaces()
    {
        var payment = New(Guid.NewGuid(), Guid.NewGuid(), amount: 123.4567m);

        await SaveAsync(payment);

        await using var context = new PaymentsDbContext(_options);
        var read = await context.Payments.SingleAsync(candidate => candidate.Id == payment.Id);

        Assert.Equal(123.4567m, read.Amount);
    }

    [Fact]
    public async Task Timestamps_ShouldRoundTripAsUtc()
    {
        var payment = New(Guid.NewGuid(), Guid.NewGuid());
        payment.Decline(AttemptedAt.AddSeconds(2));

        await SaveAsync(payment);

        await using var context = new PaymentsDbContext(_options);
        var read = await context.Payments.SingleAsync(candidate => candidate.Id == payment.Id);

        Assert.Equal(DateTimeKind.Utc, read.AttemptedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, read.ResolvedAt!.Value.Kind);
        Assert.Equal(AttemptedAt.AddSeconds(2), read.ResolvedAt);
    }

    /// <summary>A confirm and a cancel racing one row: xmin makes the second writer lose.</summary>
    [Fact]
    public async Task TwoWritersOnOneAttempt_ShouldLeaveTheLoserRejected()
    {
        var payment = New(Guid.NewGuid(), Guid.NewGuid());
        payment.Authorize("auth_1", AttemptedAt.AddSeconds(1));

        await SaveAsync(payment);

        await using var winner = new PaymentsDbContext(_options);
        await using var loser = new PaymentsDbContext(_options);

        var theirs = await winner.Payments.SingleAsync(candidate => candidate.Id == payment.Id);
        var ours = await loser.Payments.SingleAsync(candidate => candidate.Id == payment.Id);

        theirs.Capture(AttemptedAt.AddSeconds(2));
        await winner.SaveChangesAsync();

        ours.Void(AttemptedAt.AddSeconds(2));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loser.SaveChangesAsync());

        await using var check = new PaymentsDbContext(_options);
        var read = await check.Payments.SingleAsync(candidate => candidate.Id == payment.Id);

        Assert.Equal(PaymentStatus.Captured, read.Status);
    }

    private async Task SaveAsync(Payment payment)
    {
        await using var context = new PaymentsDbContext(_options);
        context.Payments.Add(payment);
        await context.SaveChangesAsync();
    }

    private static Payment New(Guid orderId, Guid clientId, decimal amount = 50m) =>
        Payment.Create(
            Guid.NewGuid(), orderId, clientId, amount, "GBP", $"key-{Guid.NewGuid():N}", AttemptedAt);

    private static Payment InStatus(Guid orderId, Guid clientId, PaymentStatus status)
    {
        var payment = New(orderId, clientId);
        var at = AttemptedAt.AddSeconds(1);

        switch (status)
        {
            case PaymentStatus.Pending:
                break;

            case PaymentStatus.Authorized:
                payment.Authorize("auth_1", at);
                break;

            case PaymentStatus.Captured:
                payment.Authorize("auth_1", at);
                payment.Capture(at);
                break;

            case PaymentStatus.Declined:
                payment.Decline(at);
                break;

            case PaymentStatus.TimedOut:
                payment.TimeOut(at);
                break;

            case PaymentStatus.Voided:
                payment.Authorize("auth_1", at);
                payment.Void(at);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status.");
        }

        return payment;
    }
}
