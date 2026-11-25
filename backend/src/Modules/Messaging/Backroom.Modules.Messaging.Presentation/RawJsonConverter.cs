using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backroom.Modules.Messaging.Presentation;

/// <summary>
/// Writes a string that already is JSON (a message's stored blocks, validated when they were written and kept in a
/// <c>jsonb</c> column) as that JSON, without parsing it into a document for every message of a page. Responses only.
/// </summary>
internal sealed class RawJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Raw JSON is written in responses only.");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRawValue(value, skipInputValidation: true);
    }
}
