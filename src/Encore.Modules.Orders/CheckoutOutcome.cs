namespace Encore.Modules.Orders;

/// <summary>
/// Every way a checkout can end. A closed set, returned rather than thrown.
/// </summary>
public enum CheckoutOutcome
{
    /// <summary>The order exists, is <c>Pending</c>, and holds every seat asked for.</summary>
    Created = 0,

    /// <summary>The request named no seats. An order with nothing on it is not a thing.</summary>
    NoSeats = 1,

    /// <summary>
    /// The same seat appeared twice. Refused, not de-duplicated.
    /// </summary>
    DuplicateSeat = 2,

    /// <summary>
    /// More seats than the published cap. Knowable from the request alone, unlike
    /// Inventory's <c>HoldCapReached</c>.
    /// </summary>
    TooManySeats = 3,

    /// <summary>Catalog has no event with that id, so nothing can be priced.</summary>
    EventNotFound = 4,

    /// <summary>Tickets are not orderable yet. The only refusal here that stops being true on its own.</summary>
    NotOnSale = 5,

    /// <summary>This client already has an open checkout for this event.</summary>
    CheckoutAlreadyOpen = 6,

    /// <summary>
    /// At least one seat could not be held. Nothing was written; seats that were held stay held.
    /// </summary>
    SeatsUnavailable = 7
}
