using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BoxForge.Models.Singbox;

namespace BoxForge.Services;

public interface IConfigSerializer
{
    string Serialize(SingboxConfig config);
    string GetContentHash(string content);
}

public sealed class ConfigSerializer : IConfigSerializer
{
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SingboxJsonDefaults.Apply } }
    };

    public string Serialize(SingboxConfig config)
    {
        return JsonSerializer.Serialize(SingboxJsonDefaults.NormalizeContext(config), jsonOptions);
    }

    public string GetContentHash(string content)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hashBytes);
    }
}
