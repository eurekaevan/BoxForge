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
    private static readonly JsonSerializerOptions ResolvedJsonOptions = new(BuiltInProfile.JsonOptions)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly BuiltInProfile data;
    private readonly HashSet<string> reservedTags;
    private readonly ImmutableArray<ImmutableDictionary<TargetPlatform, JsonElement>> platformLayers;
    private readonly TargetPlatform? effectivePlatform;

    internal BoxForgeProfile(BuiltInProfile data,
        ImmutableArray<ImmutableDictionary<TargetPlatform, JsonElement>> platformLayers = default,
        TargetPlatform? effectivePlatform = null)
    {
        this.platformLayers = platformLayers.IsDefault ? [data.Platforms] : platformLayers;
        this.effectivePlatform = effectivePlatform;
        var ruleSetTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in data.Route.RuleSets)
        {
            if (source is null || source.Tags.IsDefaultOrEmpty)
                throw new FormatException("profile.route.rule_sets.tags: 必须为非空数组。");
            foreach (string tag in source.Tags)
                ValidateId(tag, ruleSetTags, "rule_sets.tag");
            ValidateRuleSetUrl(source.Url);
            if (!GoDurationHelper.TryNormalizePositiveExtendedDuration(source.UpdateInterval, out _))
                throw new FormatException("profile.rule_sets.update_interval: 必须为有效的正 duration，且不能超出 int64 纳秒范围。");
            if (source.Tags.Length > 1 && !source.Url.Contains("{tag}", StringComparison.Ordinal))
                throw new FormatException("profile.route.rule_sets.url: 多标签来源必须包含 {tag} 占位符。");
        }
        if (effectivePlatform is not null && !ruleSetTags.IsSupersetOf([RuleSetTags.Ads, RuleSetTags.Cn, RuleSetTags.CnIp]))
            throw new FormatException("profile.route.rule_sets: 必须保留广告与国内规则集。");
        this.data = data;
        var resolverIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resolver in data.Dns.Resolvers)
        {
            if (resolver is null) throw new FormatException("profile.dns.resolvers: 元素必须为对象。");
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
        foreach (var region in data.Route.Regions)
        {
            if (region is null) throw new FormatException("profile.route.regions: 元素必须为对象。");
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
        foreach (var service in data.Route.Services)
        {
            if (service is null) throw new FormatException("profile.route.services: 元素必须为对象。");
            ValidateId(service.Id, serviceIds, "services");
            ReserveTag(service.Name, "services.name");
            if (effectivePlatform is not null && service.DefaultRegion is { } id && !ids.Contains(id))
                throw new FormatException("profile.services.default_region: 引用了不存在的地区。");
            if (service.RuleSets.IsDefaultOrEmpty || (effectivePlatform is not null && service.RuleSets.Any(tag => !ruleSetTags.Contains(tag)))
                || service.RuleSets.Distinct(StringComparer.Ordinal).Count() != service.RuleSets.Length)
                throw new FormatException("profile.services.rule_sets: 必须为非空、不重复且全部引用已声明 rule-set 的数组。");
        }
        Regions = regions.ToImmutable();
        ValidateSettings();
    }

    public static BoxForgeProfile Default { get; } = new(BuiltInProfile.Default);

    public ImmutableArray<RegionDefinition> Regions { get; }
    public FeaturesProfile Features => data.Features;
    internal DnsProfile Dns => data.Dns;
    internal RouteProfile Route => data.Route;
    public TuningProfile Tuning => data.Tuning;
    public PrivacyProfile Privacy => data.Privacy;
    public ImmutableArray<ServiceDefinition> Services => data.Route.Services;
    public PlatformProfile GetPlatform(TargetPlatform platform)
    {
        if (effectivePlatform is null) return ForPlatform(platform).GetPlatform(platform);
        if (effectivePlatform != platform) throw new ArgumentException("有效 profile 只属于其目标平台。", nameof(platform));
        return new PlatformProfile
        {
            AddressFamily = Dns.AddressFamily,
            DnsSelectionMode = Dns.SelectionMode,
            StrictRoute = Route.StrictRoute,
            AutoRedirect = Route.AutoRedirect,
            MultiQueue = Route.MultiQueue,
            UseDirectForwarding = Route.UseDirectForwarding,
            TunAddresses = Route.TunAddresses,
            ConnectTimeout = Tuning.ConnectTimeout,
            TcpKeepAlive = Tuning.TcpKeepAlive,
            TcpKeepAliveInterval = Tuning.TcpKeepAliveInterval,
            UrlTestInterval = Tuning.UrlTestInterval,
            TaildropDirectory = Features.Tailscale.TaildropDirectory
        };
    }

    public BoxForgeProfile ForPlatform(TargetPlatform platform, JsonObject? request = null)
    {
        if (!Enum.IsDefined(platform)) throw new ArgumentOutOfRangeException(nameof(platform));
        if (effectivePlatform is not null)
        {
            if (effectivePlatform != platform) throw new ArgumentException("有效 profile 只属于其目标平台。", nameof(platform));
            return request is null ? this : ApplyOverrides(request);
        }
        try
        {
            BuiltInProfile selected = data with { Platforms = ImmutableDictionary<TargetPlatform, JsonElement>.Empty };
            foreach (var layer in platformLayers)
                if (layer.TryGetValue(platform, out var patch)) selected = ProfileOverlay.Apply(selected, ReadLayer(patch));
            if (request is not null)
            {
                var patch = ProfileOverlay.Snapshot(request);
                selected = ProfileOverlay.Apply(selected, patch);
                var layers = ReadPlatforms(patch);
                if (layers.TryGetValue(platform, out var platformPatch)) selected = ProfileOverlay.Apply(selected, ReadLayer(platformPatch));
            }
            return new BoxForgeProfile(selected, [], platform);
        }
        catch (JsonException ex) { throw new FormatException("profile: 字段类型无效、缺失必填字段或存在未知字段。", ex); }
    }

    private static JsonObject ReadLayer(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("profile.platforms: 平台层必须为对象。");
        var layer = value.Deserialize<JsonObject>()!;
        if (layer.ContainsKey("platforms"))
            throw new FormatException("profile.platforms: 平台层不能包含嵌套 platforms。");
        ProfileOverlay.ValidateStructure(layer, typeof(BuiltInProfile));
        return layer;
    }

    private static ImmutableDictionary<TargetPlatform, JsonElement> ReadPlatforms(JsonObject patch)
    {
        if (!patch.TryGetPropertyValue("platforms", out var value)) return ImmutableDictionary<TargetPlatform, JsonElement>.Empty;
        if (value is not JsonObject platforms) throw new FormatException("profile.platforms: 必须为对象。");
        var result = ImmutableDictionary.CreateBuilder<TargetPlatform, JsonElement>();
        foreach (var (name, layer) in platforms)
        {
            if (!Enum.TryParse<TargetPlatform>(name, out var platform) || !Enum.IsDefined(platform) || platform.ToString() != name || layer is not JsonObject)
                throw new FormatException("profile.platforms: 必须引用目标平台且平台层必须为对象。");
            JsonElement element = JsonSerializer.SerializeToElement(layer);
            _ = ReadLayer(element);
            result.Add(platform, element);
        }
        return result.ToImmutable();
    }

    internal ImmutableArray<BuiltInProfile.RuleSetSource> RuleSets => data.Route.RuleSets;
    internal BuiltInProfile.Resolver GetResolver(string id) => data.Dns.Resolvers.Single(resolver => resolver.Id == id);

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
        try { return Default.ApplyOverrides(ProfileOverlay.Read(source)); }
        catch (JsonException ex) { throw new FormatException("profile: 覆盖文件必须是有效 JSON。", ex); }
    }

    public BoxForgeProfile ApplyOverrides(JsonObject overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        try
        {
            var patch = ProfileOverlay.Snapshot(overlay);
            var addedLayers = ReadPlatforms(patch);
            if (effectivePlatform is { } target)
            {
                var selected = ProfileOverlay.Apply(data, patch);
                if (addedLayers.TryGetValue(target, out var layer)) selected = ProfileOverlay.Apply(selected, ReadLayer(layer));
                return new BoxForgeProfile(selected, [], target);
            }
            return new BoxForgeProfile(ProfileOverlay.Apply(data, patch), platformLayers.Add(addedLayers));
        }
        catch (JsonException ex) { throw new FormatException("profile: 字段类型无效、缺失必填字段或存在未知字段。", ex); }
    }

    public string ToResolvedJson()
    {
        if (effectivePlatform is not null) return JsonSerializer.Serialize(data, ResolvedJsonOptions);
        var platforms = Enum.GetValues<TargetPlatform>().ToImmutableDictionary(platform => platform, platform =>
        {
            var json = JsonSerializer.SerializeToNode(ForPlatform(platform).data, BuiltInProfile.JsonOptions)!.AsObject();
            json.Remove("platforms");
            return JsonSerializer.SerializeToElement(json);
        });
        return JsonSerializer.Serialize(data with { Platforms = platforms }, ResolvedJsonOptions);
    }

    private void ValidateSettings()
    {
        if (effectivePlatform is null)
        {
            foreach (var target in Enum.GetValues<TargetPlatform>()) _ = ForPlatform(target);
            return;
        }
        if (!data.Route.Regions.Any(region => region.Id == Route.MainDefaultRegion))
            throw new FormatException("profile.route.main_default_region: 必须引用已声明地区。");
        TargetPlatform platform = effectivePlatform.Value;
        var settings = GetPlatform(platform);
        string path = $"profile.platforms.{platform}";
        if (!Enum.IsDefined(settings.AddressFamily) || !Enum.IsDefined(settings.DnsSelectionMode))
            throw new FormatException($"{path}: 无效的地址族或 DNS 模式。");
        if (platform != TargetPlatform.Linux && settings.MultiQueue == true)
            throw new FormatException($"{path}.multi_queue: 仅支持 Linux。");
        Duration(settings.ConnectTimeout, path + ".connect_timeout");
        Duration(settings.UrlTestInterval, path + ".url_test_interval");
        if (settings.TcpKeepAlive is { } keepAlive) Duration(keepAlive, path + ".tcp_keep_alive");
        if (settings.TcpKeepAliveInterval is { } interval) Duration(interval, path + ".tcp_keep_alive_interval");
        if (string.IsNullOrWhiteSpace(settings.TaildropDirectory) || settings.TaildropDirectory.Any(char.IsControl))
            throw new FormatException($"{path}.taildrop_directory: 不能为空或包含控制字符。");
        var families = new HashSet<AddressFamily>();
        foreach (string address in settings.TunAddresses)
        {
            if (address is null) throw new FormatException($"{path}.tun_addresses: 元素必须为 CIDR 字符串。");
            string[] parts = address.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.ToString() != parts[0]
                || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int prefix)
                || prefix < 1 || prefix > (ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)
                || ip.IsIPv4MappedToIPv6 || !families.Add(ip.AddressFamily))
                throw new FormatException($"{path}.tun_addresses: 必须是规范且不重复地址族的 CIDR 地址。");
        }
        if (!families.SetEquals([AddressFamily.InterNetwork, AddressFamily.InterNetworkV6]))
            throw new FormatException($"{path}.tun_addresses: 必须同时提供 IPv4 与 IPv6 捕获地址。");
        Duration(Route.SniffTimeout, "profile.route.sniff_timeout");
        Duration(Tuning.UrlTestIdleTimeout, "profile.tuning.url_test_idle_timeout");
        Duration(Dns.Timeout, "profile.dns.timeout");
        Duration(Dns.OptimisticTimeout, "profile.dns.optimistic_timeout");
        if (!Uri.TryCreate(Tuning.UrlTestUrl, UriKind.Absolute, out var probe) || probe.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(probe.Host) || !string.IsNullOrEmpty(probe.UserInfo) || !string.IsNullOrEmpty(probe.Fragment)
            || Tuning.UrlTestUrl.Any(char.IsWhiteSpace) || !probe.IsWellFormedOriginalString())
            throw new FormatException("profile.tuning.url_test_url: 必须是有效的 HTTP(S) URL，不含用户信息或片段。");
        if (Tuning.UrlTestTolerance < 0 || Dns.CacheCapacity == 0
            || Route.SniffExcludedPorts.Any(port => port is < 1 or > 65535)
            || Route.SniffExcludedPorts.Distinct().Count() != Route.SniffExcludedPorts.Length)
            throw new FormatException("profile: tolerance 必须非负，缓存容量必须为正，排除端口必须有效且唯一。");
    }

    private static void Duration(string value, string path)
    {
        if (!GoDurationHelper.IsPositiveDuration(value))
            throw new FormatException($"{path}: 必须为有效的正 Go duration，且不能超出 int64 纳秒范围。");
    }

    private static void ValidateId(string id, HashSet<string> ids, string section)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 32 || id[0] is < 'a' or > 'z'
            || id.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) || !ids.Add(id))
            throw new FormatException($"profile.{section}: id 必须唯一，且为以小写字母开头的 1–32 位小写字母、数字或连字符。");
    }

    private void ReserveTag(string tag, string field)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag != tag.Trim() || tag.Any(char.IsControl) || !reservedTags.Add(tag))
            throw new FormatException($"profile.{field}: 名称不能为空、含控制字符或首尾空白，也不能与其他地区、服务或保留 tag 冲突。");
    }

    internal static void ValidateRuleSetUrl(string url)
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
