using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtocolValue
{
    public required string Kind { get; init; }
    public JsonElement? Value { get; init; }

    public static ProtocolValue From(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => new ProtocolValue { Kind = "null" },
            JsonValueKind.String => Create("string", value),
            JsonValueKind.Number => Create("number", value),
            JsonValueKind.True or JsonValueKind.False => Create("boolean", value),
            JsonValueKind.Array => Create("array", value),
            JsonValueKind.Object => Create("object", value),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.ValueKind, "Unsupported JSON value kind")
        };

    public static ProtocolValue FromString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return Create("string", document.RootElement);
    }

    private static ProtocolValue Create(string kind, JsonElement value) =>
        new()
        {
            Kind = kind,
            Value = value.Clone()
        };
}
