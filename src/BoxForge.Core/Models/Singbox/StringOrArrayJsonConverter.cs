using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

public sealed class StringOrArrayJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return [reader.GetString()!];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a string or an array of strings.");
        }

        var values = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return values;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected an array of strings.");
            }

            values.Add(reader.GetString()!);
        }

        throw new JsonException("Unterminated array of strings.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<string> value,
        JsonSerializerOptions options)
    {
        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (string item in value)
        {
            writer.WriteStringValue(item);
        }
        writer.WriteEndArray();
    }
}
