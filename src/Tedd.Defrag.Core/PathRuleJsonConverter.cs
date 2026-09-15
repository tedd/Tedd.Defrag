using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tedd.Defrag.Core;

/// <summary>Reads legacy string rules and writes explicit per-rule match kinds.</summary>
public sealed class PathRuleJsonConverter : JsonConverter<PathRule>
{
    public override PathRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new(reader.GetString() ?? "");
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("A path rule must be a string or object.");

        string? pattern = null;
        PathRuleKind kind = PathRuleKind.Path;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Invalid path rule.");
            string property = reader.GetString() ?? "";
            if (!reader.Read()) throw new JsonException("Incomplete path rule.");
            if (property.Equals(nameof(PathRule.Pattern), StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType != JsonTokenType.String) throw new JsonException("Path-rule pattern must be a string.");
                pattern = reader.GetString();
            }
            else if (property.Equals(nameof(PathRule.Kind), StringComparison.OrdinalIgnoreCase))
            {
                kind = reader.TokenType switch
                {
                    JsonTokenType.String when Enum.TryParse<PathRuleKind>(reader.GetString(), true, out var parsed) => parsed,
                    JsonTokenType.Number when reader.TryGetInt32(out var value) && Enum.IsDefined((PathRuleKind)value) => (PathRuleKind)value,
                    _ => throw new JsonException("Unknown path-rule kind.")
                };
            }
            else reader.Skip();
        }
        return new(pattern ?? throw new JsonException("Path-rule pattern is required."), kind);
    }

    public override void Write(Utf8JsonWriter writer, PathRule value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(PathRule.Pattern), value.Pattern);
        writer.WriteString(nameof(PathRule.Kind), value.Kind.ToString());
        writer.WriteEndObject();
    }
}
