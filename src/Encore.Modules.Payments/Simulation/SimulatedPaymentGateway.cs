namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// A fake payment provider. Randomly succeeds, declines or times out according
/// to <see cref="PaymentSimulationOptions"/>, after a configurable delay.
/// There is no real provider behind this and there is not meant to be — the
/// point is to give the rest of the system a dependency that is slow and
/// unreliable in a way we control.
/// </summary>
public sealed class SimulatedPaymentGateway
{
    // TODO: ChargeAsync(orderId, amount, cancellationToken) -> success | declined | timeout.
}
