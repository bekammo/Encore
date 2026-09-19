using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>
/// Proves Payments' schema enforces what the module is relying on it to enforce.
/// </summary>
/// <remarks>
/// The partial unique index is the one that matters. It is the real guard against
/// a retried confirm charging a customer twice, and it is expressed as a raw SQL
/// filter string that no compiler checks against <see cref="PaymentStatus"/> — so
/// a wrong literal produces a silently different index rather than a build error.
/// These tests are what would catch that, and they are the reason
/// <c>PaymentTests.IsLive_ShouldMatchTheIndexFilter</c> is worth anything.
/// </remarks>
public sealed class PaymentsSchemaTests : IAsyncLifetime
{
    private static readonly DateTime AttemptedAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private DbContextOptions<PaymentsDbContext> _options = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UsePaymentsNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new PaymentsDbContext(_options);

        // Migrate rather than EnsureCreated: this also proves the generated
        // migration applies against real Postgres, including that it does not try
        // to create the xmin system column.
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Migrate_ShouldLeaveNoPendingMigrations()
    {
        await using var context = new PaymentsDbContext(_options);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    /// <summary>
    /// The guard that makes a retried confirm safe. Two live attempts against one
    /// order must be impossible at the database, not merely checked for in code —
    /// two requests can both pass a check.
    /// </summary>
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

    /// <summary>
    /// The other half of the same rule, and the half a too-broad filter would
    /// break. A declined attempt moved no money, so it must not stop the customer
    /// trying again with a different card — and a voided one released what it
    /// held, so it must not either.
    /// </summary>
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

    /// <summary>
    /// Four decimal places, per <c>DECISIONS.md</c> 016. A price that rounds on
    /// the way into the database is a price somebody disputes later.
    /// </summary>
    [Fact]
    public async Task Amount_ShouldRoundTripAtFourDecimalPlaces()
    {
        var payment = New(Guid.NewGuid(), Guid.NewGuid(), amount: 123.4567m);

        await SaveAsync(payment);

        await using var context = new PaymentsDbContext(_options);
        var read = await context.Payments.SingleAsync(candidate => candidate.Id == payment.Id);

        Assert.Equal(123.4567m, read.Amount);
    }

    /// <summary>
    /// Postgres <c>timestamptz</c> stores UTC and discards the offset, and Npgsql
    /// rejects a non-UTC <see cref="DateTime"/> outright. Both timestamps have to
    /// come back with <see cref="DateTimeKind.Utc"/> or every comparison above
    /// this line is against a value of unknown meaning.
    /// </summary>
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

    /// <summary>
    /// The token that stops a confirm and a cancel racing this row from writing
    /// <see cref="PaymentStatus.Voided"/> over money that was taken. Both writers
    /// load the row while it still reads <see cref="PaymentStatus.Authorized"/>,
    /// so nothing in C# can catch this — only the database can.
    /// </summary>
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
