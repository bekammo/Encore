namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// What came back from a void attempt.
/// </summary>
/// <param name="Status">How it turned out.</param>
/// <param name="PaymentId">The attempt that was released, if there was one.</param>
public sealed record VoidPaymentResponse(
    VoidPaymentStatus Status,
    Guid? PaymentId = null)
{
    /// <summary><see cref="VoidPaymentStatus.Voided"/>.</summary>
    public static VoidPaymentResponse Voided(Guid paymentId) =>
        new(VoidPaymentStatus.Voided, paymentId);

    /// <summary><see cref="VoidPaymentStatus.NoAuthorization"/>.</summary>
    public static VoidPaymentResponse NoAuthorization { get; } =
        new(VoidPaymentStatus.NoAuthorization);

    /// <summary><see cref="VoidPaymentStatus.AlreadyCaptured"/>.</summary>
    public static VoidPaymentResponse AlreadyCaptured(Guid paymentId) =>
        new(VoidPaymentStatus.AlreadyCaptured, paymentId);

    /// <summary><see cref="VoidPaymentStatus.TimedOut"/>.</summary>
    public static VoidPaymentResponse TimedOut(Guid paymentId) =>
        new(VoidPaymentStatus.TimedOut, paymentId);
}
