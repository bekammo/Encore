namespace Encore.Modules.Notifications.Models;

/// <summary>What a notification is about.</summary>
/// <remarks>
/// One member, because one event has a consumer. Appended to, never reordered:
/// the value is stored as a plain integer, so inserting a member would silently
/// re-label every row already written — the same trap
/// <c>ux_orders_client_event_pending</c> sets by filtering on the literal
/// <c>"Status" = 0</c>.
/// </remarks>
public enum NotificationKind
{
    /// <summary>A seat this client held has been confirmed as sold to them.</summary>
    SeatSold = 0
}
