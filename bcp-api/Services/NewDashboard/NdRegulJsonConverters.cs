using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Models not bound by a JSON schema (OpenRouter, etc.) sometimes return a list where a single
/// text field is expected; join it instead of failing the whole clause.</summary>
public abstract class JsonFlexibleStringConverter(string separator) : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return "";
            case JsonTokenType.String:
                return reader.GetString() ?? "";
            case JsonTokenType.Number:
            case JsonTokenType.True:
            case JsonTokenType.False:
                using (var doc = JsonDocument.ParseValue(ref reader))
                    return doc.RootElement.GetRawText();
            case JsonTokenType.StartArray:
                var parts = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var item = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(item)) parts.Add(item.Trim());
                    }
                    else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        using var doc = JsonDocument.ParseValue(ref reader);
                        parts.Add(doc.RootElement.GetRawText());
                    }
                }
                return string.Join(separator, parts);
            default:
                using (var doc = JsonDocument.ParseValue(ref reader))
                    return doc.RootElement.GetRawText();
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

public sealed class JsonSemicolonJoinedStringConverter() : JsonFlexibleStringConverter("; ");

public sealed class JsonLineJoinedStringConverter() : JsonFlexibleStringConverter("\n");

/// <summary>LLM sometimes returns a single string instead of an array for policy_extract.</summary>
public sealed class JsonStringOrArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return [];
            case JsonTokenType.String:
                var single = reader.GetString();
                return string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()];
            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var item = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(item))
                            list.Add(item.Trim());
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                        reader.Skip();
                }
                return list;
            default:
                reader.Skip();
                return [];
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}
