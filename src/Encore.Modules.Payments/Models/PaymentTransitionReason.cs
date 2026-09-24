namespace Encore.Modules.Payments.Models;

public enum PaymentTransitionReason
{
    NotPending = 0,
    NotAuthorized = 1,
    AlreadyCaptured = 2,
    NotTimedOut = 3
}
