namespace Encore.Modules.Notifications.Models;

/// <summary>What a notification is about.</summary>
/// <remarks>Stored as an integer: append members, never reorder.</remarks>
public enum NotificationKind
{
    /// <summary>A seat this client held has been confirmed as sold to them.</summary>
    SeatSold = 0
}
