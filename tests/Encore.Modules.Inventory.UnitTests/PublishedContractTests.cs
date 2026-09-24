using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Contracts.Events;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// Outbox rows were written in these shapes: changing one is a new version, not an edit.
/// </summary>
public sealed class PublishedContractTests
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

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Publishing_ShouldWriteThePinnedPayload(object contract, string payload) =>
        Assert.Equal(
            payload,
            JsonSerializer.Serialize(contract, contract.GetType(), SeatEventPublication.SerializerOptions));

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Reading_WithoutInventorysOptions_ShouldGiveBackTheSameContract(object contract, string payload) =>
        Assert.Equal(contract, JsonSerializer.Deserialize(payload, contract.GetType()));

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
