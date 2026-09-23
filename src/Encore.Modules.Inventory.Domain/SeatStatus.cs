namespace Encore.Modules.Inventory.Domain;

/// <summary>
/// The persisted state of a <see cref="Seat"/>. A <see cref="Held"/> row whose hold has
/// lapsed is logically available; nothing may wait for the sweep to fix the column.
/// </summary>
public enum SeatStatus
{
    Available = 0,

    /// <summary>Claimed by a client until <see cref="Seat.HoldExpiresAt"/>.</summary>
    Held = 1,

    /// <summary>Terminal.</summary>
    Sold = 2
}
