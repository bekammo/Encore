namespace Encore.Modules.Inventory.Domain;

/// <summary>
/// The persisted state of a <see cref="Seat"/>.
/// </summary>
/// <remarks>
/// This is what is written in Postgres, which is not always the same as what is
/// logically true: a row reading <see cref="Held"/> whose
/// <see cref="Seat.HoldExpiresAt"/> has passed is logically available, and every
/// path that touches a seat is required to treat it that way rather than trust
/// the column. The background sweep eventually reconciles the two, but only as
/// hygiene — nothing may depend on it having run.
/// </remarks>
public enum SeatStatus
{
    /// <summary>Nobody holds this seat and it can be held.</summary>
    Available = 0,

    /// <summary>Claimed by a client until <see cref="Seat.HoldExpiresAt"/>.</summary>
    Held = 1,

    /// <summary>Terminal. A sold seat never returns to the available pool.</summary>
    Sold = 2
}
