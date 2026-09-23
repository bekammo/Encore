using Encore.Modules.Catalog.Data;
using Encore.Modules.Catalog.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.Endpoints;

/// <summary>
/// Endpoints for browsing and populating the catalogue: straight CRUD against
/// <see cref="CatalogDbContext"/>. No client identity: the catalogue is public and writes
/// are operator-facing. Ids are generated here.
/// </summary>
public static class CatalogEndpoints
{
    private const int MaxNameLength = 200;

    private const int MaxAddressLength = 500;

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var catalog = endpoints.MapGroup("/catalog");

        catalog.MapPost("/venues", CreateVenueAsync)
            .WithName("CreateVenue")
            .WithSummary("Creates a venue and returns it.");

        catalog.MapGet("/venues", ListVenuesAsync)
            .WithName("ListVenues")
            .WithSummary("Lists every venue.");

        catalog.MapGet("/venues/{venueId:guid}", GetVenueAsync)
            .WithName("GetVenue")
            .WithSummary("Reads one venue.");

        catalog.MapPost("/events", CreateEventAsync)
            .WithName("CreateEvent")
            .WithSummary("Creates an event at an existing venue and returns it.");

        catalog.MapGet("/events", ListEventsAsync)
            .WithName("ListEvents")
            .WithSummary("Lists every event, soonest first.");

        catalog.MapGet("/events/{eventId:guid}", GetEventAsync)
            .WithName("GetEvent")
            .WithSummary("Reads one event.");

        return endpoints;
    }

    private static async Task<IResult> CreateVenueAsync(
        CreateVenueRequest request,
        CatalogDbContext catalog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaxNameLength)
        {
            return CatalogResults.Invalid(
                context.Request.Path,
                "Invalid venue",
                $"Name is required and must be at most {MaxNameLength} characters.",
                "invalid_name");
        }

        if (string.IsNullOrWhiteSpace(request.Address) || request.Address.Length > MaxAddressLength)
        {
            return CatalogResults.Invalid(
                context.Request.Path,
                "Invalid venue",
                $"Address is required and must be at most {MaxAddressLength} characters.",
                "invalid_address");
        }

        if (request.Capacity < 1)
        {
            return CatalogResults.Invalid(
                context.Request.Path,
                "Invalid venue",
                "Capacity must be at least 1.",
                "invalid_capacity");
        }

        var venue = new Venue
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Address = request.Address,
            Capacity = request.Capacity
        };

        catalog.Venues.Add(venue);
        await catalog.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Created($"/catalog/venues/{venue.Id}", ToResponse(venue));
    }

    private static async Task<IResult> ListVenuesAsync(
        CatalogDbContext catalog,
        CancellationToken cancellationToken)
    {
        var venues = await catalog.Venues
            .AsNoTracking()
            .OrderBy(venue => venue.Name)
            .Select(venue => new VenueResponse(venue.Id, venue.Name, venue.Address, venue.Capacity))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(venues);
    }

    private static async Task<IResult> GetVenueAsync(
        Guid venueId,
        CatalogDbContext catalog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var venue = await catalog.Venues
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == venueId, cancellationToken)
            .ConfigureAwait(false);

        return venue is null
            ? CatalogResults.NotFound(
                context.Request.Path, "Venue not found", "No such venue.", "venue_not_found")
            : TypedResults.Ok(ToResponse(venue));
    }

    private static async Task<IResult> CreateEventAsync(
        CreateEventRequest request,
        CatalogDbContext catalog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaxNameLength)
        {
            return CatalogResults.Invalid(
                context.Request.Path,
                "Invalid event",
                $"Name is required and must be at most {MaxNameLength} characters.",
                "invalid_name");
        }

        if (!TryToUtc(request.StartsAt, out var startsAt))
        {
            return AmbiguousTimestamp(context.Request.Path, nameof(request.StartsAt));
        }

        DateTime? onSaleAt = null;

        if (request.OnSaleAt is { } requestedOnSaleAt)
        {
            if (!TryToUtc(requestedOnSaleAt, out var normalised))
            {
                return AmbiguousTimestamp(context.Request.Path, nameof(request.OnSaleAt));
            }

            onSaleAt = normalised;
        }

        if (request.Price < 0)
        {
            return CatalogResults.Invalid(
                context.Request.Path, "Invalid event", "Price cannot be negative.", "invalid_price");
        }

        if (!IsCurrencyCode(request.Currency))
        {
            return CatalogResults.Invalid(
                context.Request.Path,
                "Invalid event",
                "Currency must be a three-letter ISO 4217 code.",
                "invalid_currency");
        }

        // Checked rather than constrained, so the client gets a readable answer.
        var venueExists = await catalog.Venues
            .AsNoTracking()
            .AnyAsync(venue => venue.Id == request.VenueId, cancellationToken)
            .ConfigureAwait(false);

        if (!venueExists)
        {
            // 409, not 404: the addressed route exists; the venue named in the body does not.
            return CatalogResults.Conflict(
                context.Request.Path,
                "venue_not_found",
                "No venue with that id. Create the venue before the event.",
                retriable: false);
        }

        var show = new Event
        {
            Id = Guid.NewGuid(),
            VenueId = request.VenueId,
            Name = request.Name,
            StartsAt = startsAt,
            OnSaleAt = onSaleAt,
            Price = request.Price,
            Currency = request.Currency.ToUpperInvariant()
        };

        catalog.Events.Add(show);
        await catalog.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Created($"/catalog/events/{show.Id}", ToResponse(show));
    }

    private static async Task<IResult> ListEventsAsync(
        CatalogDbContext catalog,
        CancellationToken cancellationToken)
    {
        var events = await catalog.Events
            .AsNoTracking()
            .OrderBy(show => show.StartsAt)
            .Select(show => new EventResponse(
                show.Id,
                show.VenueId,
                show.Name,
                show.StartsAt,
                show.OnSaleAt,
                show.Price,
                show.Currency))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(events);
    }

    private static async Task<IResult> GetEventAsync(
        Guid eventId,
        CatalogDbContext catalog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var show = await catalog.Events
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == eventId, cancellationToken)
            .ConfigureAwait(false);

        return show is null
            ? CatalogResults.NotFound(
                context.Request.Path, "Event not found", "No such event.", "event_not_found")
            : TypedResults.Ok(ToResponse(show));
    }

    /// <summary>
    /// Normalises an instant to UTC, refusing one with no timezone. Guessing would put a
    /// show on sale at the wrong instant.
    /// </summary>
    private static bool TryToUtc(DateTime value, out DateTime utc)
    {
        utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => default
        };

        return value.Kind is not DateTimeKind.Unspecified;
    }

    private static IResult AmbiguousTimestamp(PathString path, string field) =>
        CatalogResults.Invalid(
            path,
            "Ambiguous timestamp",
            $"{field} must carry a timezone: end it with Z for UTC, or give an offset.",
            "ambiguous_timestamp");

    /// <summary>
    /// Three ASCII letters; a shape check, not a copy of ISO 4217.
    /// </summary>
    private static bool IsCurrencyCode(string? currency) =>
        currency is { Length: 3 } && currency.All(char.IsAsciiLetter);

    private static VenueResponse ToResponse(Venue venue) =>
        new(venue.Id, venue.Name, venue.Address, venue.Capacity);

    private static EventResponse ToResponse(Event show) =>
        new(
            show.Id,
            show.VenueId,
            show.Name,
            show.StartsAt,
            show.OnSaleAt,
            show.Price,
            show.Currency);
}
