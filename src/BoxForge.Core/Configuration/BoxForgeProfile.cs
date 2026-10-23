using BoxForge.Exceptions;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoxForge.Models;

namespace BoxForge.Configuration;

/// <summary>An immutable configuration document with ordered platform overlay layers.</summary>
public sealed class BoxForgeProfile
{
    private readonly BuiltInProfile data;
    private readonly ImmutableArray<ImmutableDictionary<TargetPlatform, JsonElement>> platformLayers;

    internal BoxForgeProfile(BuiltInProfile data,
        ImmutableArray<ImmutableDictionary<TargetPlatform, JsonElement>> platformLayers = default,
        string origin = "profile.document")
    {
        this.data = data;
        this.platformLayers = platformLayers.IsDefault ? [data.Platforms] : platformLayers;
        try { _ = ResolvedProfile.ValidateDefinitions(data, validateReferences: false); }
        catch (ProfileValidationException ex) { throw ex.AtOrigin(origin); }
        foreach (var platform in Enum.GetValues<TargetPlatform>()) _ = Resolve(platform);
    }

    public static BoxForgeProfile Default { get; } = new(BuiltInProfile.Default);

    public FeaturesProfile Features => data.Features;
    internal RouteProfile Route => data.Route;
    public TuningProfile Tuning => data.Tuning;
    public PrivacyProfile Privacy => data.Privacy;
    public ImmutableArray<ServiceDefinition> Services => data.Route.Services;

    public ResolvedProfile Resolve(TargetPlatform platform, JsonObject? request = null)
    {
        if (!Enum.IsDefined(platform)) throw new ArgumentOutOfRangeException(nameof(platform));
        try
        {
            BuiltInProfile selected = data with { Platforms = ImmutableDictionary<TargetPlatform, JsonElement>.Empty };
            for (int index = 0; index < platformLayers.Length; index++)
                if (platformLayers[index].TryGetValue(platform, out var patch))
                {
                    string path = $"profile.platform_layers[{index}].{platform}";
                    selected = ProfileOverlay.Apply(selected, ReadLayer(patch, path), path);
                }
            if (request is not null)
            {
                JsonObject patch;
                try { patch = ProfileOverlay.Snapshot(request); }
                catch (ProfileValidationException ex) { throw ex.AtOrigin("profile.request"); }
                selected = ProfileOverlay.Apply(selected, patch, "profile.request");
                var layers = ReadPlatforms(patch, "profile.request");
                if (layers.TryGetValue(platform, out var platformPatch))
                {
                    string path = $"profile.request.platforms.{platform}";
                    selected = ProfileOverlay.Apply(selected, ReadLayer(platformPatch, path), path);
                }
            }
            try { return new ResolvedProfile(selected, platform); }
            catch (ProfileValidationException ex) { throw ex.AtOrigin($"profile.effective.{platform}"); }
        }
        catch (JsonException ex) { throw new ProfileValidationException("profile", "profile: 字段类型无效、缺失必填字段或存在未知字段。", ex); }
    }

    private static JsonObject ReadLayer(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ProfileValidationException(path, "profile.platforms: 平台层必须为对象。");
        var layer = value.Deserialize<JsonObject>()!;
        if (layer.ContainsKey("platforms"))
            throw new ProfileValidationException(path, "profile.platforms: 平台层不能包含嵌套 platforms。");
        ProfileOverlay.ValidateStructure(layer, typeof(BuiltInProfile), path);
        return layer;
    }

    private static ImmutableDictionary<TargetPlatform, JsonElement> ReadPlatforms(JsonObject patch, string path)
    {
        if (!patch.TryGetPropertyValue("platforms", out var value)) return ImmutableDictionary<TargetPlatform, JsonElement>.Empty;
        if (value is not JsonObject platforms) throw new ProfileValidationException(path + ".platforms", "profile.platforms: 必须为对象。");
        var result = ImmutableDictionary.CreateBuilder<TargetPlatform, JsonElement>();
        foreach (var (name, layer) in platforms)
        {
            if (!Enum.TryParse<TargetPlatform>(name, out var platform) || !Enum.IsDefined(platform) || platform.ToString() != name)
                throw new ProfileValidationException(path + ".platforms[unknown]", "profile.platforms: 必须引用目标平台且平台层必须为对象。");
            JsonElement element = JsonSerializer.SerializeToElement(layer);
            _ = ReadLayer(element, path + ".platforms." + name);
            result.Add(platform, element);
        }
        return result.ToImmutable();
    }

    public static BoxForgeProfile Load(string? path)
    {
        if (path is null) return Default;
        if (string.IsNullOrWhiteSpace(path))
            throw new ProfileValidationException("profile.file", "profile: 文件路径不能为空。");
        try
        {
            using var source = File.OpenRead(path);
            return Load(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileValidationException("profile.file", "profile: 无法读取覆盖文件，请检查路径和文件权限。", ex);
        }
    }

    internal static BoxForgeProfile Load(Stream source)
    {
        try { return Default.ApplyOverrides(ProfileOverlay.Read(source), "profile.file"); }
        catch (ProfileValidationException ex) when (ex.Diagnostics[0].Path == "profile") { throw ex.AtOrigin("profile.file"); }
        catch (JsonException ex) { throw new ProfileValidationException("profile.file", "profile: 覆盖文件必须是有效 JSON。", ex); }
    }

    public BoxForgeProfile ApplyOverrides(JsonObject overlay) => ApplyOverrides(overlay, "profile.overlay");

    internal BoxForgeProfile ApplyOverrides(JsonObject overlay, string origin)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        try
        {
            var patch = ProfileOverlay.Snapshot(overlay);
            var addedLayers = ReadPlatforms(patch, origin);
            return new BoxForgeProfile(ProfileOverlay.Apply(data, patch, origin), platformLayers.Add(addedLayers), origin);
        }
        catch (ProfileValidationException ex) when (ex.Diagnostics[0].Path == "profile") { throw ex.AtOrigin(origin); }
        catch (JsonException ex) { throw new ProfileValidationException("profile", "profile: 字段类型无效、缺失必填字段或存在未知字段。", ex); }
    }

    public string ToResolvedJson()
    {
        var platforms = Enum.GetValues<TargetPlatform>().ToImmutableDictionary(platform => platform,
            platform => Resolve(platform).ToPlatformSnapshot());
        return JsonSerializer.Serialize(data with { Platforms = platforms }, BuiltInProfile.SnapshotJsonOptions);
    }
}
