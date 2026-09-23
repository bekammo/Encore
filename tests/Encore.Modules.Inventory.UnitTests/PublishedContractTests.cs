using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Contracts.Events;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The wire shape of every published contract, pinned to a checked-in payload. Rows already in
/// the outbox were written in this shape, so changing it is a new version, not an edit.
/// </summary>
public class PublishedContractTests
{
    private static readonly Guid SeatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTime At = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public static TheoryData<object, string> Contracts => new()
    {
        {
            new SeatHeldV1(SeatId, EventId, ClientId, At.AddMinutes(5), At),
            """{"seatId":"11111111-1111-1111-1111-111111111111","eventId":"22222222-2222-2222-2222-222222222222","clientId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","holdExpiresAt":"2026-01-01T12:05:00Z","occurredAt":"2026-01-01T12:00:00Z"}"""
        },
        {
            new SeatReleasedV1(SeatId, EventId, ClientId, SeatReleasedV1.Expired, At),
            """{"seatId":"11111111-1111-1111-1111-111111111111","eventId":"22222222-2222-2222-2222-222222222222","clientId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","reason":"expired","occurredAt":"2026-01-01T12:00:00Z"}"""
        },
        {
            new SeatSoldV1(SeatId, EventId, ClientId, At),
            """{"seatId":"11111111-1111-1111-1111-111111111111","eventId":"22222222-2222-2222-2222-222222222222","clientId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","occurredAt":"2026-01-01T12:00:00Z"}"""
        }
    };

    /// <summary>Inventory writes exactly the pinned payload.</summary>
    [Theory]
    [MemberData(nameof(Contracts))]
    public void Publishing_ShouldWriteThePinnedPayload(object contract, string payload) =>
        Assert.Equal(payload, JsonSerializer.Serialize(contract, contract.GetType(), SeatEventPublication.SerializerOptions));

    /// <summary>
    /// A consumer holding only the contracts assembly, reading with default options, gets the
    /// same record back: the wire names travel with the contract, not with Inventory's options.
    /// </summary>
    [Theory]
    [MemberData(nameof(Contracts))]
    public void Reading_WithoutInventorysOptions_ShouldGiveBackTheSameContract(object contract, string payload) =>
        Assert.Equal(contract, JsonSerializer.Deserialize(payload, contract.GetType()));

    /// <summary>
    /// A row missing a member fails to read, so it is dead-lettered where someone can see it,
    /// instead of reaching a handler as <c>Guid.Empty</c> and being marked delivered.
    /// </summary>
    [Theory]
    [MemberData(nameof(Contracts))]
    public void Reading_APayloadMissingAMember_ShouldFailRatherThanDefaultIt(object contract, string payload)
    {
        using var document = JsonDocument.Parse(payload);

        var withoutTheFirst = JsonSerializer.Serialize(
            document.RootElement.EnumerateObject().Skip(1).ToDictionary(member => member.Name, member => member.Value));

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(withoutTheFirst, contract.GetType(), SeatEventPublication.SerializerOptions));
    }
}
