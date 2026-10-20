using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BoxForge.Models.Singbox;

namespace BoxForge.Services;

internal interface IConfigSerializer
{
    string Serialize(SingboxConfig config);
    string GetContentHash(string content);
}

internal sealed class ConfigSerializer : IConfigSerializer
{
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ConfigureSerialization } }
    };

    private static void ConfigureSerialization(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type.Namespace != typeof(SingboxConfig).Namespace) return;
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            property.ShouldSerialize = static (_, value) => value is not null
                && value is not ICollection { Count: 0 };
        }
    }

    public string Serialize(SingboxConfig config)
    {
        return JsonSerializer.Serialize(config, jsonOptions);
    }

    public string GetContentHash(string content)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hashBytes);
    }
}
