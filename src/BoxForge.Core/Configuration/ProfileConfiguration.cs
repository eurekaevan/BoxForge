using BoxForge.Exceptions;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoxForge.Configuration;

internal static class ProfileConfiguration
{
    private static readonly string[] Sections = ["features", "dns", "route", "tuning", "privacy", "platforms"];
    private static readonly string[] LegacyKeys =
        ["Tailscale", "TailscaleEnabled", "TailscaleAndroidEnabled", "SingboxApi"];

    internal static bool IsProfileSection(string key) => Sections.Contains(key, StringComparer.OrdinalIgnoreCase);

    internal static BoxForgeProfile ResolveProfile(IConfiguration configuration)
    {
        foreach (string key in LegacyKeys)
            if (configuration.GetSection(key).Exists()) throw new ProfileValidationException("profile.host", $"配置键 '{key}' 已删除，请使用 profile 路径。");
        if (configuration.GetSection("Profile").GetChildren().Any())
            throw new ProfileValidationException("profile.host", "Profile 只接受文件路径，不能包含子键。");
        var profile = BoxForgeProfile.Load(configuration["Profile"]);
        var values = configuration.AsEnumerable().Where(pair => pair.Value is not null
            && IsProfileSection(pair.Key.Split(':')[0])).ToArray();
        if (values.Length == 0) return profile;
        if (values.Any(parent => values.Any(child => child.Key.StartsWith(parent.Key + ":", StringComparison.OrdinalIgnoreCase))))
            throw new ProfileValidationException("profile.host", "配置不能同时设置对象与其子字段。");
        var patch = new JsonObject();
        JsonObject schema = JsonSerializer.SerializeToNode(BuiltInProfile.Default, BuiltInProfile.JsonOptions)!.AsObject();
        foreach (var (key, value) in values)
        {
            string[] path = key.Split(':');
            Set(patch, schema, path, Parse(value!));
        }
        return profile.ApplyOverrides(patch, "profile.host");
    }

    private static JsonNode? Parse(string value)
    {
        if (bool.TryParse(value, out bool boolean)) return JsonValue.Create(boolean);
        try { return JsonNode.Parse(value); }
        catch (JsonException) { return JsonValue.Create(value); }
    }

    private static void Set(JsonObject target, JsonObject schema, ReadOnlySpan<string> path, JsonNode? value)
    {
        string segment = path[0];
        string? key = schema.Select(pair => pair.Key).SingleOrDefault(key => key.Equals(segment, StringComparison.OrdinalIgnoreCase));
        if (key is null) throw new ProfileValidationException("profile.host", "配置路径不是已声明的 profile 键。");
        if (key == "platforms")
        {
            if (path.Length < 3 || !Enum.TryParse<BoxForge.Models.TargetPlatform>(path[1], true, out var platform) || !Enum.IsDefined(platform)
                || !path[1].Equals(platform.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new ProfileValidationException("profile.host", "platforms 宿主配置必须指定目标平台及其字段。");
            var platforms = target[key] as JsonObject;
            if (platforms is null) target[key] = platforms = new JsonObject();
            var layer = platforms[platform.ToString()] as JsonObject;
            if (layer is null) platforms[platform.ToString()] = layer = new JsonObject();
            Set(layer, schema, path[2..], value);
            return;
        }
        if (path.Length == 1)
        {
            if (target.ContainsKey(key)) throw new ProfileValidationException("profile.host", "配置不能同时设置对象与其子字段。");
            target[key] = value;
            return;
        }
        if (schema[key] is not JsonObject childSchema) throw new ProfileValidationException("profile.host", "宿主配置必须直接设置标量或完整数组操作。");
        if (target.TryGetPropertyValue(key, out var existing) && existing is not JsonObject)
            throw new ProfileValidationException("profile.host", "配置不能同时设置对象与其子字段。");
        var child = existing as JsonObject ?? new JsonObject();
        if (existing is null) target[key] = child;
        Set(child, childSchema, path[1..], value);
    }
}
