using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoxForge.Configuration;

internal static class ProfileOverlay
{
    internal static JsonObject Read(Stream source)
    {
        using var document = JsonDocument.Parse(source);
        Validate(document.RootElement, "profile");
        return document.RootElement.Deserialize<JsonObject>(BuiltInProfile.JsonOptions)
            ?? throw new FormatException("profile: 必须为对象。");
    }

    private static void Validate(JsonElement value, string path)
    {
        try
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new FormatException($"{path}: 存在重复键。");
                    Validate(property.Value, $"{path}.{property.Name}");
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray()) Validate(item, path + "[]");
            }
            else if (value.ValueKind == JsonValueKind.String)
                _ = value.GetString(); // Decode escaped UTF-16 now, before lazy JsonNode materialization.
            else if (value.ValueKind == JsonValueKind.Null && !AllowsNull(path))
                throw new FormatException($"{path}: 不能为 null。");
        }
        catch (InvalidOperationException ex)
        {
            throw new FormatException("profile: JSON 名称与字符串必须是有效的 Unicode。", ex);
        }
    }

    private static bool AllowsNull(string path) => path.Split('.') is
        ["profile", "platforms", "Android" or "Linux" or "Windows",
            "strict_route" or "auto_redirect" or "multi_queue" or "tcp_keep_alive" or "tcp_keep_alive_interval"];

    internal static BuiltInProfile Apply(BuiltInProfile original, JsonObject overlay)
    {
        // Validate caller-owned JSON too; no mutable JSON nodes enter the snapshot.
        string json;
        try { json = overlay.ToJsonString(); }
        catch (InvalidOperationException ex)
        {
            // A lazy JsonObject can fail while decoding an escaped property name.
            throw new FormatException("profile: JSON 名称与字符串必须是有效的 Unicode。", ex);
        }
        using var document = JsonDocument.Parse(json);
        Validate(document.RootElement, "profile");
        if (overlay["version"] is not JsonValue version || !version.TryGetValue<int>(out int number) || number != 1)
            throw new FormatException("profile.version: 必须为整数 1。");
        var target = JsonSerializer.SerializeToNode(original, BuiltInProfile.JsonOptions)!.AsObject();
        foreach (var (name, value) in overlay)
        {
            if (!target.ContainsKey(name)) throw new FormatException($"profile.{name}: 存在未知或重复的键。");
            if (name == "rule_sets") continue;
            if (name is "regions" or "services" or "resolvers")
            {
                MergeEntries(target[name]!.AsArray(), value as JsonArray
                    ?? throw new FormatException($"profile.{name}: 必须为数组。"), name);
            }
            else MergeProperty(target, name, value, "profile");
        }
        var sources = overlay.TryGetPropertyValue("rule_sets", out var rules)
            ? MergeRuleSets(original.RuleSets, rules as JsonArray
                ?? throw new FormatException("profile.rule_sets: 必须为数组。"))
            : original.RuleSets;
        if (JsonNode.DeepEquals(target, JsonSerializer.SerializeToNode(original, BuiltInProfile.JsonOptions)))
            return original with { RuleSets = sources };
        var merged = target.Deserialize<BuiltInProfile>(BuiltInProfile.JsonOptions)
            ?? throw new FormatException("profile: 必须为对象。");
        return merged with { RuleSets = sources };
    }

    private static void MergeProperty(JsonObject target, string name, JsonNode? value, string path)
    {
        if (!target.ContainsKey(name)) throw new FormatException($"{path}.{name}: 存在未知或重复的键。");
        if (target[name] is JsonObject current)
        {
            if (value is not JsonObject patch) throw new FormatException($"{path}.{name}: 必须为对象。");
            foreach (var (key, child) in patch) MergeProperty(current, key, child, $"{path}.{name}");
        }
        else target[name] = value?.DeepClone();
    }

    private static IEnumerable<(string Key, JsonObject Item)> Entries(JsonArray entries, string key, string section)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in entries)
        {
            if (item is not JsonObject entry || entry[key] is not JsonValue id || !id.TryGetValue<string>(out var name))
                throw new FormatException($"profile.{section}.{key}: 必须为字符串且必填。");
            if (!seen.Add(name)) throw new FormatException($"profile.{section}.{key}: 不能重复。");
            yield return (name, entry);
        }
    }

    private static void MergeEntries(JsonArray target, JsonArray overlay, string section)
    {
        foreach (var (id, item) in Entries(overlay, "id", section))
        {
            var current = target.OfType<JsonObject>().SingleOrDefault(entry => entry["id"]!.GetValue<string>() == id);
            if (current is null)
            {
                if (section == "resolvers") throw new FormatException("profile.resolvers.id: 必须引用现有 resolver ID。");
                current = (JsonObject)item.DeepClone();
                if (section == "services")
                {
                    if (!current.ContainsKey("default_region")) current["default_region"] = null;
                    if (!current.ContainsKey("precedes_domestic_routes")) current["precedes_domestic_routes"] = false;
                }
                target.Add(current);
            }
            else foreach (var (name, value) in item) MergeProperty(current, name, value, $"profile.{section}");
        }
    }

    private static ImmutableArray<BuiltInProfile.RuleSetSource> MergeRuleSets(
        ImmutableArray<BuiltInProfile.RuleSetSource> sources, JsonArray overlay)
    {
        var originals = sources.SelectMany(source => source.Tags.Select(tag => (tag, source)))
            .ToDictionary(entry => entry.tag, entry => entry.source, StringComparer.Ordinal);
        var replacements = new Dictionary<string, BuiltInProfile.RuleSetSource>(StringComparer.Ordinal);
        var additions = new List<BuiltInProfile.RuleSetSource>();
        foreach (var (tag, item) in Entries(overlay, "tag", "rule_sets"))
        {
            originals.TryGetValue(tag, out var original);
            var merged = new JsonObject { ["tag"] = tag, ["url"] = original?.Url, ["update_interval"] = original?.UpdateInterval ?? "1d" };
            foreach (var (key, value) in item) MergeProperty(merged, key, value, "profile.rule_sets");
            var rule = merged.Deserialize<RuleSetOverride>(BuiltInProfile.JsonOptions)!;
            if (rule.Url is null) throw new FormatException("profile.rule_sets.url: 新规则集必填。");
            BoxForgeProfile.ValidateRuleSetUrl(rule.Url);
            if (original is null || rule.Url != original.Url || rule.UpdateInterval != original.UpdateInterval)
            {
                var replacement = new BuiltInProfile.RuleSetSource([tag], rule.Url.Replace("{tag}", tag, StringComparison.Ordinal), rule.UpdateInterval);
                if (original is null) additions.Add(replacement);
                else replacements.Add(tag, replacement);
            }
        }
        if (replacements.Count == 0 && additions.Count == 0) return sources;
        var result = ImmutableArray.CreateBuilder<BuiltInProfile.RuleSetSource>();
        foreach (var source in sources)
        {
            var remaining = source.Tags.Where(tag => !replacements.ContainsKey(tag)).ToImmutableArray();
            if (!remaining.IsEmpty)
                result.Add(source with { Tags = remaining, Url = remaining.Length == 1 ? source.Url.Replace("{tag}", remaining[0], StringComparison.Ordinal) : source.Url });
            foreach (string tag in source.Tags)
                if (replacements.TryGetValue(tag, out var replacement)) result.Add(replacement);
        }
        result.AddRange(additions);
        return result.ToImmutable();
    }

    private sealed record RuleSetOverride(string Tag, string? Url, string UpdateInterval);
}
