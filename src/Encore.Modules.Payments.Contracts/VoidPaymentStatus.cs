namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// How a void attempt turned out. A closed set.
/// </summary>
public enum VoidPaymentStatus
{
    /// <summary>The hold is released and nothing was taken.</summary>
    Voided = 0,

    /// <summary>
    /// Nothing is held against this order, including a repeated void. Not an error.
    /// </summary>
    NoAuthorization = 1,

    /// <summary>
    /// The money has already been taken; releasing it would be a refund.
    /// </summary>
    AlreadyCaptured = 2,

    /// <summary>
    /// The gateway did not answer. The hold lapses at the gateway on its own.
    /// </summary>
    TimedOut = 3
}
