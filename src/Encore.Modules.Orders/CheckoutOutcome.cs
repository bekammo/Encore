namespace Encore.Modules.Orders;

/// <summary>
/// Every way a checkout can end. A closed set, so the HTTP mapping can switch
/// exhaustively and adding a case breaks the build rather than falling through.
/// </summary>
/// <remarks>
/// Refusals are return values here, not exceptions, for the reason
/// <c>DECISIONS.md</c> 008 gives for Inventory: losing a seat to somebody else
/// is the most ordinary outcome there is during a flash sale, and ordinary
/// outcomes do not get to unwind the stack.
/// </remarks>
public enum CheckoutOutcome
{
    /// <summary>The order exists, is <c>Pending</c>, and holds every seat asked for.</summary>
    Created = 0,

    /// <summary>The request named no seats. An order with nothing on it is not a thing.</summary>
    NoSeats = 1,

    /// <summary>
    /// The same seat appeared twice. Refused rather than de-duplicated — see
    /// <c>DECISIONS.md</c> 025.
    /// </summary>
    DuplicateSeat = 2,

    /// <summary>
    /// More seats than one client may hold at one event. Knowable from the
    /// request alone, which is what separates it from
    /// <see cref="Encore.Modules.Inventory.Contracts.HoldSeatStatus.HoldCapReached"/>.
    /// </summary>
    TooManySeats = 3,

    /// <summary>Catalog has no event with that id, so nothing can be priced.</summary>
    EventNotFound = 4,

    /// <summary>Tickets are not orderable yet. The only refusal here that stops being true on its own.</summary>
    NotOnSale = 5,

    /// <summary>This client already has an open checkout for this event.</summary>
    CheckoutAlreadyOpen = 6,

    /// <summary>
    /// At least one seat could not be held. Nothing was written, and the seats
    /// that were held stay held — see <c>DECISIONS.md</c> 023.
    /// </summary>
    SeatsUnavailable = 7
}
