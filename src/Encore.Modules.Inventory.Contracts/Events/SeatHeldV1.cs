namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>
/// Published when a seat has been claimed for a client.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not <c>Inventory.Domain.Events.SeatHeld</c>, and the duplication is
/// the point.</b> Serialising the domain record directly would be cheaper and would
/// make <c>Seat</c>'s field names a wire format: a rename inside the aggregate would
/// then be a breaking change to every consumer, including rows already sitting in
/// the outbox. That is 003's argument — a port speaks the module's language, never
/// the adapter's — applied to the payload rather than to a signature.
/// </para>
/// <para>
/// Zero attributes on purpose. This assembly holds no <c>PackageReference</c>
/// (<c>ENCORE001</c>–<c>003</c>), and a serialiser's attributes would be the first
/// thing to arrive through that door. The module owns the serialisation options.
/// </para>
/// </remarks>
/// <param name="SeatId">The seat that was claimed.</param>
/// <param name="EventId">The concert the seat belongs to.</param>
/// <param name="ClientId">Who claimed it.</param>
/// <param name="HoldExpiresAt">When this claim lapses if nothing else happens. UTC.</param>
/// <param name="OccurredAt">When the claim was made. UTC.</param>
public sealed record SeatHeldV1(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    DateTime HoldExpiresAt,
    DateTime OccurredAt);
