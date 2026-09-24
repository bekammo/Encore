namespace Encore.Modules.Payments.Contracts;

public enum VoidPaymentStatus
{
    Voided = 0,

    /// <summary>Also the answer to a repeated void.</summary>
    NoAuthorization = 1,

    AlreadyCaptured = 2,

    /// <summary>The hold lapses at the gateway on its own.</summary>
    TimedOut = 3
}
