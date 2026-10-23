using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using BoxForge.Models;

namespace BoxForge.Configuration;

internal sealed record BuiltInProfile(
    ImmutableDictionary<TargetPlatform, JsonElement> Platforms,
    FeaturesProfile Features,
    DnsProfile Dns,
    RouteProfile Route,
    TuningProfile Tuning,
    PrivacyProfile Privacy)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonOptions)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static BuiltInProfile Default { get; } = LoadDefault();

    internal static BuiltInProfile Load(Stream source)
    {
        var profile = JsonSerializer.Deserialize<BuiltInProfile>(source, JsonOptions)
            ?? throw new JsonException("内置 profile 不能为空。");
        if (!profile.Platforms.Keys.ToHashSet().SetEquals(Enum.GetValues<TargetPlatform>()))
            throw new JsonException("内置 profile 必须包含所有目标平台。");
        return profile;
    }

    private static BuiltInProfile LoadDefault()
    {
        using Stream source = typeof(BuiltInProfile).Assembly.GetManifestResourceStream(
            "BoxForge.Configuration.default-profile.json")
            ?? throw new InvalidOperationException("缺少内置 profile 资源。");
        return Load(source);
    }

    internal sealed record Region(string Id, string DisplayName, string Pattern);

    internal sealed record RuleSetSource(
        ImmutableArray<string> Tags, string Url, string UpdateInterval);

    internal sealed record Resolver(string Id, string Server, string ServerName);
}
