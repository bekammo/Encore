namespace Encore.Modules.Catalog.Models;

public sealed class Venue
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    /// <summary>Descriptive only: sellable seats come from the event's seat map.</summary>
    public int Capacity { get; set; }
}
