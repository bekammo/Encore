namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The "put this seat in my basket" use case. Thin by design: read the clock,
/// take the hold, load the aggregate, let the domain decide whether the
/// transition is legal, persist. Every rule it appears to enforce actually
/// lives in <see cref="Domain.Seat"/>; this class only sequences the ports.
/// </summary>
public sealed class HoldSeatCommandHandler
{
}
