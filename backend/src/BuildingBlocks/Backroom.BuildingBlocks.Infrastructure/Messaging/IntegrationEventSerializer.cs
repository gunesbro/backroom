using System.Text.Json;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>JSON for integration events: snake_case property names, as in the contracts.</summary>
public static class IntegrationEventSerializer
{
    public const string ContentType = "application/json";

    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static byte[] Serialize<TEvent>(TEvent integrationEvent) =>
        JsonSerializer.SerializeToUtf8Bytes(integrationEvent, s_options);

    public static TEvent? Deserialize<TEvent>(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<TEvent>(payload, s_options);
}
