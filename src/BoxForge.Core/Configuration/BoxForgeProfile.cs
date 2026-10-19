using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BoxForge.Models;
using BoxForge.Helpers;
using System.Net;
using System.Net.Sockets;

namespace BoxForge.Configuration;

/// <summary>An immutable profile shared by node validation and configuration generation.</summary>
public sealed class BoxForgeProfile
{
    private readonly BuiltInProfile data;
    private readonly HashSet<string> reservedTags;

    internal BoxForgeProfile(BuiltInProfile data)
    {
        var sources = ImmutableArray.CreateBuilder<BuiltInProfile.RuleSetSource>();
        var ruleSetTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in data.RuleSets)
        {
            foreach (string tag in source.Tags)
                ValidateId(tag, ruleSetTags, "rule_sets.tag");
            ValidateRuleSetUrl(source.Url);
            if (!GoDurationHelper.TryNormalizePositiveExtendedDuration(source.UpdateInterval, out string interval))
                throw new FormatException("profile.rule_sets.update_interval: 必须为有效的正 duration，且不能超出 int64 纳秒范围。");
            sources.Add(source with { UpdateInterval = interval });
        }
        this.data = data with { RuleSets = sources.ToImmutable() };
        var resolverIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resolver in data.Resolvers)
        {
            if (!resolverIds.Add(resolver.Id))
                throw new FormatException("profile.resolvers.id: 不能重复。");
            if (!IPAddress.TryParse(resolver.Server, out var address)
                || address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != resolver.Server
                || address.GetAddressBytes()[0] is 0 or >= 224)
                throw new FormatException("profile.resolvers.server: 必须为规范的单播 IPv4 字面量，不能使用域名、IPv6、0.* 或首段大于等于 224 的地址。");
            if (resolver.ServerName.Length is < 1 or > 253 || IPAddress.TryParse(resolver.ServerName, out _)
                || resolver.ServerName.Split('.').Any(label => label.Length is < 1 or > 63
                    || label[0] == '-' || label[^1] == '-'
                    || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
                throw new FormatException("profile.resolvers.server_name: 必须为有效的 ASCII DNS 名称，不能为 IP、URL 或通配符。");
        }
        if (!resolverIds.SetEquals(["alidns", "tencent", "cloudflare", "google"]))
            throw new FormatException("profile.resolvers: 必须保留四个固定 resolver ID。");
        reservedTags = new HashSet<string>(StringComparer.Ordinal)
        {
            SingboxTags.MainProxyGroup,
            SingboxTags.DirectOutbound,
            SingboxTags.BridgeOutbound,
            SingboxTags.TailscaleEndpoint,
            SingboxTags.TailscaleDns,
            SingboxTags.DirectAliDns,
            SingboxTags.DirectTencentDns,
            SingboxTags.ProxyGoogleDns,
            SingboxTags.ProxyCloudflareDns,
            SingboxTags.TunInbound,
            SingboxTags.ApiService,
            HttpClientTags.RuleSetProxy,
            HttpClientTags.DashboardDirect
        };
        reservedTags.UnionWith(DnsResponseTags.BaseTags);
        reservedTags.UnionWith(DnsResponseTags.BaseTags.Select(DnsResponseTags.ForAddressQuery));
        foreach (string tag in ruleSetTags)
            ReserveTag(tag, "rule_sets.tag");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var regions = ImmutableArray.CreateBuilder<RegionDefinition>();
        foreach (var region in data.Regions)
        {
            ValidateId(region.Id, ids, "regions");
            ReserveTag(region.DisplayName, "regions.display_name");
            ReserveTag($"{region.DisplayName} AUTO", "regions.display_name");
            if (string.IsNullOrWhiteSpace(region.Pattern))
                throw new FormatException("profile.regions.pattern: 匹配表达式不能为空。");
            try
            {
                regions.Add(new RegionDefinition(region.Id, region.DisplayName,
                    new Regex(region.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250))));
            }
            catch (ArgumentException ex)
            {
                throw new FormatException("profile.regions.pattern: 无效的正则表达式。", ex);
            }
        }
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in data.Services)
        {
            ValidateId(service.Id, serviceIds, "services");
            ReserveTag(service.Name, "services.name");
            if (service.DefaultRegion is { } id && !ids.Contains(id))
                throw new FormatException("profile.services.default_region: 引用了不存在的地区。");
            if (service.RuleSets.IsDefaultOrEmpty || service.RuleSets.Any(tag => !ruleSetTags.Contains(tag))
                || service.RuleSets.Distinct(StringComparer.Ordinal).Count() != service.RuleSets.Length)
                throw new FormatException("profile.services.rule_sets: 必须为非空、不重复且全部引用已声明 rule-set 的数组。");
        }
        Regions = regions.ToImmutable();
    }

    public static BoxForgeProfile Default { get; } = new(BuiltInProfile.Default);

    public ImmutableArray<RegionDefinition> Regions { get; }
    public ImmutableArray<ServiceDefinition> Services => data.Services;
    public ImmutableDictionary<TargetPlatform, PlatformProfile> Platforms => data.Platforms;

    public PlatformProfile GetPlatform(TargetPlatform platform) =>
        Platforms.TryGetValue(platform, out var settings)
            ? settings
            : throw new ArgumentOutOfRangeException(nameof(platform), platform, null);

    internal ImmutableArray<BuiltInProfile.RuleSetSource> RuleSets => data.RuleSets;
    internal BuiltInProfile.Resolver GetResolver(string id) => data.Resolvers.Single(resolver => resolver.Id == id);

    public bool IsReservedTag(string tag) => reservedTags.Contains(tag);

    public static BoxForgeProfile Load(string? path)
    {
        if (path is null) return Default;
        if (string.IsNullOrWhiteSpace(path))
            throw new FormatException("profile: 文件路径不能为空。");
        try
        {
            using var source = File.OpenRead(path);
            return Load(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FormatException("profile: 无法读取覆盖文件，请检查路径和文件权限。", ex);
        }
    }

    internal static BoxForgeProfile Load(Stream source)
    {
        try
        {
            using var document = JsonDocument.Parse(source);
            var root = document.RootElement;
            ValidateKeys(root, "version", "regions", "services", "rule_sets", "resolvers");
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int number) || number != 1)
                throw new FormatException("profile.version: 必须为整数 1。");
            var regions = BuiltInProfile.Default.Regions.ToList();
            foreach (var (id, item) in ReadOverrides(root, "regions", "id", "display_name", "pattern"))
            {
                int index = regions.FindIndex(region => region.Id == id);
                var original = index < 0 ? null : regions[index];
                var region = new BuiltInProfile.Region(id,
                    ReadString(item, "display_name") ?? original?.DisplayName
                        ?? throw new FormatException("profile.regions.display_name: 新地区必填。"),
                    ReadString(item, "pattern") ?? original?.Pattern
                        ?? throw new FormatException("profile.regions.pattern: 新地区必填。"));
                if (index < 0) regions.Add(region);
                else regions[index] = region;
            }
            var services = BuiltInProfile.Default.Services.ToList();
            foreach (var (id, item) in ReadOverrides(root, "services", "id", "name", "default_region", "rule_sets", "precedes_domestic_routes"))
            {
                int index = services.FindIndex(service => service.Id == id);
                var original = index < 0 ? null : services[index];
                ImmutableArray<string> ruleSets;
                if (item.TryGetProperty("rule_sets", out var value))
                {
                    if (value.ValueKind != JsonValueKind.Array
                        || value.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String))
                        throw new FormatException("profile.services.rule_sets: 必须为字符串数组，不能为 null。");
                    ruleSets = [.. value.EnumerateArray().Select(tag => tag.GetString()!)];
                }
                else ruleSets = original?.RuleSets ?? throw new FormatException("profile.services.rule_sets: 新服务必填。");
                bool precedesDomestic = original?.PrecedesDomesticRoutes ?? false;
                if (item.TryGetProperty("precedes_domestic_routes", out value))
                {
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new FormatException("profile.services.precedes_domestic_routes: 必须为布尔值。");
                    precedesDomestic = value.GetBoolean();
                }
                var service = new ServiceDefinition(id,
                    ReadString(item, "name") ?? original?.Name
                        ?? throw new FormatException("profile.services.name: 新服务必填。"),
                    ReadString(item, "default_region") ?? original?.DefaultRegion,
                    ruleSets, precedesDomestic);
                if (index < 0) services.Add(service);
                else services[index] = service;
            }
            var ruleSetSources = BuiltInProfile.Default.RuleSets;
            var originals = ruleSetSources.SelectMany(source => source.Tags.Select(tag => (tag, source)))
                .ToDictionary(entry => entry.tag, entry => entry.source, StringComparer.Ordinal);
            var replacements = new Dictionary<string, BuiltInProfile.RuleSetSource>(StringComparer.Ordinal);
            var additions = new List<BuiltInProfile.RuleSetSource>();
            foreach (var (tag, item) in ReadOverrides(root, "rule_sets", "tag", "url", "update_interval"))
            {
                originals.TryGetValue(tag, out var original);
                string url = ReadString(item, "url") ?? original?.Url
                    ?? throw new FormatException("profile.rule_sets.url: 新规则集必填。");
                string interval = ReadString(item, "update_interval") ?? original?.UpdateInterval ?? "1d";
                ValidateRuleSetUrl(url);
                if (original is null || url != original.Url || interval != original.UpdateInterval)
                {
                    var replacement = new BuiltInProfile.RuleSetSource([tag],
                        url.Replace("{tag}", tag, StringComparison.Ordinal), interval);
                    if (original is null) additions.Add(replacement);
                    else replacements.Add(tag, replacement);
                }
            }
            if (replacements.Count > 0 || additions.Count > 0)
            {
                var sources = ImmutableArray.CreateBuilder<BuiltInProfile.RuleSetSource>();
                foreach (var original in ruleSetSources)
                {
                    var remaining = original.Tags.Where(tag => !replacements.ContainsKey(tag)).ToImmutableArray();
                    if (!remaining.IsEmpty)
                        sources.Add(original with
                        {
                            Tags = remaining,
                            Url = remaining.Length == 1 ? original.Url.Replace("{tag}", remaining[0], StringComparison.Ordinal) : original.Url
                        });
                    foreach (string tag in original.Tags)
                        if (replacements.TryGetValue(tag, out var replacement)) sources.Add(replacement);
                }
                sources.AddRange(additions);
                ruleSetSources = sources.ToImmutable();
            }
            var resolvers = BuiltInProfile.Default.Resolvers.ToList();
            foreach (var (id, item) in ReadOverrides(root, "resolvers", "id", "server", "server_name"))
            {
                int index = resolvers.FindIndex(resolver => resolver.Id == id);
                if (index < 0)
                    throw new FormatException("profile.resolvers.id: 必须引用现有 resolver ID。");
                var original = resolvers[index];
                resolvers[index] = original with
                {
                    Server = ReadString(item, "server") ?? original.Server,
                    ServerName = ReadString(item, "server_name") ?? original.ServerName
                };
            }
            return new BoxForgeProfile(BuiltInProfile.Default with
            { Regions = [.. regions], Services = [.. services], RuleSets = ruleSetSources, Resolvers = [.. resolvers] });
        }
        catch (JsonException ex)
        {
            throw new FormatException("profile: 覆盖文件必须是有效 JSON。", ex);
        }
    }

    private static IEnumerable<(string Key, JsonElement Item)> ReadOverrides(
        JsonElement root, string section, string key, params string[] fields)
    {
        if (!root.TryGetProperty(section, out var overrides)) yield break;
        if (overrides.ValueKind != JsonValueKind.Array)
            throw new FormatException($"profile.{section}: 必须为数组。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in overrides.EnumerateArray())
        {
            ValidateKeys(item, [key, .. fields]);
            string id = ReadString(item, key) ?? throw new FormatException($"profile.{section}.{key}: 必填。");
            if (!seen.Add(id)) throw new FormatException($"profile.{section}.{key}: 覆盖文件中不能重复。");
            yield return (id, item);
        }
    }

    public string ToResolvedJson(TailscaleOptions tailscale, SingboxApiOptions api)
    {
        var options = new JsonSerializerOptions(BuiltInProfile.JsonOptions)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var root = JsonSerializer.SerializeToNode(data, options)!;
        root["runtime"] = new JsonObject
        {
            ["tailscale"] = new JsonObject { ["enabled"] = tailscale.Enabled, ["android_enabled"] = tailscale.AndroidEnabled },
            ["singbox_api"] = new JsonObject { ["enabled"] = api.Enabled }
        };
        return root.ToJsonString(options);
    }

    private static void ValidateKeys(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("profile: 根及覆盖条目必须是对象。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new FormatException($"profile: 存在未知或重复的键；此处只允许 {string.Join(", ", allowed)}。");
    }

    private static string? ReadString(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new FormatException($"profile.{key}: 必须为字符串，不能为 null。");
        return value.GetString();
    }

    private static void ValidateId(string id, HashSet<string> ids, string section)
    {
        if (id.Length is < 1 or > 32 || id[0] is < 'a' or > 'z'
            || id.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) || !ids.Add(id))
            throw new FormatException($"profile.{section}: id 必须唯一，且为以小写字母开头的 1–32 位小写字母、数字或连字符。");
    }

    private void ReserveTag(string tag, string field)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag != tag.Trim() || tag.Any(char.IsControl) || !reservedTags.Add(tag))
            throw new FormatException($"profile.{field}: 名称不能为空、含控制字符或首尾空白，也不能与其他地区、服务或保留 tag 冲突。");
    }

    private static void ValidateRuleSetUrl(string url)
    {
        string expanded = url.Replace("{tag}", "boxforge-tag", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsWhiteSpace) || url.Any(char.IsControl)
            || expanded.Contains('{') || expanded.Contains('}')
            || !Uri.TryCreate(expanded, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new FormatException("profile.rule_sets.url: 必须为绝对 HTTPS URL，不含空白、用户信息、片段或未知占位符。");
        int authorityEnd = url.IndexOfAny(['/', '?', '#'], url.IndexOf("://", StringComparison.Ordinal) + 3);
        if (url[..(authorityEnd < 0 ? url.Length : authorityEnd)].Contains("{tag}", StringComparison.Ordinal))
            throw new FormatException("profile.rule_sets.url: {tag} 只能位于路径或查询部分。");
    }
}
