using BoxForge.Exceptions;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using BoxForge.Helpers;
using BoxForge.Models;

namespace BoxForge.Configuration;

/// <summary>A validated, immutable configuration bound to one target platform.</summary>
public sealed class ResolvedProfile
{
    private readonly BuiltInProfile data;
    private readonly ImmutableHashSet<string> reservedTags;

    internal ResolvedProfile(BuiltInProfile data, TargetPlatform platform)
    {
        this.data = data;
        Platform = platform;
        (Regions, reservedTags) = ValidateDefinitions(data, validateReferences: true);
        Settings = new PlatformProfile
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
        ValidateSettings();
    }

    public ImmutableArray<RegionDefinition> Regions { get; }
    public FeaturesProfile Features => data.Features;
    internal DnsProfile Dns => data.Dns;
    internal RouteProfile Route => data.Route;
    public TuningProfile Tuning => data.Tuning;
    public PrivacyProfile Privacy => data.Privacy;
    public ImmutableArray<ServiceDefinition> Services => data.Route.Services;
    public TargetPlatform Platform { get; }
    public PlatformProfile Settings { get; }

    internal ImmutableArray<BuiltInProfile.RuleSetSource> RuleSets => data.Route.RuleSets;
    internal BuiltInProfile.Resolver GetResolver(string id) => data.Dns.Resolvers.Single(resolver => resolver.Id == id);
    public bool IsReservedTag(string tag) => reservedTags.Contains(tag);
    public string ToResolvedJson() => JsonSerializer.Serialize(data, BuiltInProfile.SnapshotJsonOptions);
    internal JsonElement ToPlatformSnapshot()
    {
        var json = JsonSerializer.SerializeToNode(data, BuiltInProfile.JsonOptions)!.AsObject();
        json.Remove("platforms");
        return JsonSerializer.SerializeToElement(json);
    }

    internal static (ImmutableArray<RegionDefinition> Regions, ImmutableHashSet<string> ReservedTags) ValidateDefinitions(
        BuiltInProfile data, bool validateReferences)
    {
        var ruleSetTags = new HashSet<string>(StringComparer.Ordinal);
        for (int sourceIndex = 0; sourceIndex < data.Route.RuleSets.Length; sourceIndex++)
        {
            var source = data.Route.RuleSets[sourceIndex];
            string sourcePath = $"profile.route.rule_sets[{sourceIndex}]";
            if (source is null || source.Tags.IsDefaultOrEmpty)
                throw new ProfileValidationException(sourcePath + ".tags", "profile.route.rule_sets.tags: 必须为非空数组。");
            for (int tag = 0; tag < source.Tags.Length; tag++)
                ValidateId(source.Tags[tag], ruleSetTags, $"route.rule_sets[{sourceIndex}].tags[{tag}]");
            ValidateRuleSetUrl(source.Url, sourcePath + ".url");
            if (!GoDurationHelper.TryNormalizePositiveExtendedDuration(source.UpdateInterval, out _))
                throw new ProfileValidationException(sourcePath + ".update_interval", "profile.rule_sets.update_interval: 必须为有效的正 duration，且不能超出 int64 纳秒范围。");
            if (source.Tags.Length > 1 && !source.Url.Contains("{tag}", StringComparison.Ordinal))
                throw new ProfileValidationException(sourcePath + ".url", "profile.route.rule_sets.url: 多标签来源必须包含 {tag} 占位符。");
        }
        if (validateReferences && !ruleSetTags.IsSupersetOf([RuleSetTags.Ads, RuleSetTags.Cn, RuleSetTags.CnIp]))
            throw new ProfileValidationException("profile.route.rule_sets", "profile.route.rule_sets: 必须保留广告与国内规则集。");
        var resolverIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < data.Dns.Resolvers.Length; index++)
        {
            var resolver = data.Dns.Resolvers[index];
            string resolverPath = $"profile.dns.resolvers[{index}]";
            if (resolver is null) throw new ProfileValidationException("profile.dns.resolvers", "profile.dns.resolvers: 元素必须为对象。");
            if (!resolverIds.Add(resolver.Id))
                throw new ProfileValidationException(resolverPath + ".id", "profile.resolvers.id: 不能重复。");
            if (!IPAddress.TryParse(resolver.Server, out var address)
                || address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != resolver.Server
                || address.GetAddressBytes()[0] is 0 or >= 224)
                throw new ProfileValidationException(resolverPath + ".server", "profile.resolvers.server: 必须为规范的单播 IPv4 字面量，不能使用域名、IPv6、0.* 或首段大于等于 224 的地址。");
            if (resolver.ServerName.Length is < 1 or > 253 || IPAddress.TryParse(resolver.ServerName, out _)
                || resolver.ServerName.Split('.').Any(label => label.Length is < 1 or > 63
                    || label[0] == '-' || label[^1] == '-'
                    || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
                throw new ProfileValidationException(resolverPath + ".server_name", "profile.resolvers.server_name: 必须为有效的 ASCII DNS 名称，不能为 IP、URL 或通配符。");
        }
        if (!resolverIds.SetEquals(["alidns", "tencent", "cloudflare", "google"]))
            throw new ProfileValidationException("profile.dns.resolvers", "profile.resolvers: 必须保留四个固定 resolver ID。");
        var reservedTags = new HashSet<string>(StringComparer.Ordinal)
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
        reservedTags.UnionWith(DnsResponseTags.BaseTags.Select(tag => DnsResponseTags.ForAddressQuery(tag, "AAAA")));
        for (int source = 0; source < data.Route.RuleSets.Length; source++)
            for (int tag = 0; tag < data.Route.RuleSets[source].Tags.Length; tag++)
                ReserveTag(data.Route.RuleSets[source].Tags[tag], $"route.rule_sets[{source}].tags[{tag}]");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var regions = ImmutableArray.CreateBuilder<RegionDefinition>();
        for (int index = 0; index < data.Route.Regions.Length; index++)
        {
            var region = data.Route.Regions[index];
            string regionPath = $"route.regions[{index}]";
            if (region is null) throw new ProfileValidationException("profile.route.regions", "profile.route.regions: 元素必须为对象。");
            ValidateId(region.Id, ids, regionPath + ".id");
            ReserveTag(region.DisplayName, regionPath + ".display_name");
            ReserveTag($"{region.DisplayName} AUTO", regionPath + ".display_name");
            if (string.IsNullOrWhiteSpace(region.Pattern))
                throw new ProfileValidationException("profile." + regionPath + ".pattern", "profile.regions.pattern: 匹配表达式不能为空。");
            try
            {
                regions.Add(new RegionDefinition(region.Id, region.DisplayName,
                    new Regex(region.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250))));
            }
            catch (ArgumentException ex)
            {
                throw new ProfileValidationException("profile." + regionPath + ".pattern", "profile.regions.pattern: 无效的正则表达式。", ex);
            }
        }
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < data.Route.Services.Length; index++)
        {
            var service = data.Route.Services[index];
            string servicePath = $"route.services[{index}]";
            if (service is null) throw new ProfileValidationException("profile.route.services", "profile.route.services: 元素必须为对象。");
            ValidateId(service.Id, serviceIds, servicePath + ".id");
            ReserveTag(service.Name, servicePath + ".name");
            if (validateReferences && service.DefaultRegion is { } id && !ids.Contains(id))
                throw new ProfileValidationException("profile." + servicePath + ".default_region", "profile.services.default_region: 引用了不存在的地区。");
            if (service.RuleSets.IsDefaultOrEmpty || (validateReferences && service.RuleSets.Any(tag => !ruleSetTags.Contains(tag)))
                || service.RuleSets.Distinct(StringComparer.Ordinal).Count() != service.RuleSets.Length)
                throw new ProfileValidationException("profile." + servicePath + ".rule_sets", "profile.services.rule_sets: 必须为非空、不重复且全部引用已声明 rule-set 的数组。");
        }
        for (int index = 0; index < data.Route.Services.Length; index++)
        {
            var service = data.Route.Services[index];
            string servicePath = $"route.services[{index}]";
            ReserveTag(SingboxTags.ServiceDns(service.Id, "cloudflare"), servicePath + ".id");
            ReserveTag(SingboxTags.ServiceDns(service.Id, "google"), servicePath + ".id");
            foreach (string suffix in new[] { "primary", "secondary", "primary-a", "secondary-a", "primary-aaaa", "secondary-aaaa" })
                ReserveTag($"response-service-{service.Id}-{suffix}", servicePath + ".id");
        }
        return (regions.ToImmutable(), reservedTags.ToImmutableHashSet(StringComparer.Ordinal));

        void ReserveTag(string tag, string field)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag != tag.Trim() || tag.Any(char.IsControl) || !reservedTags.Add(tag))
                throw new ProfileValidationException($"profile.{field}", $"profile.{field}: 名称不能为空、含控制字符或首尾空白，也不能与其他地区、服务或保留 tag 冲突。");
        }
    }

    private void ValidateSettings()
    {
        if (!data.Route.Regions.Any(region => region.Id == Route.MainDefaultRegion))
            throw new ProfileValidationException("profile.route.main_default_region", "profile.route.main_default_region: 必须引用已声明地区。");
        TargetPlatform platform = Platform;
        var settings = Settings;
        string path = $"profile.platforms.{platform}";
        if (!Enum.IsDefined(settings.AddressFamily) || !Enum.IsDefined(settings.DnsSelectionMode))
            throw new ProfileValidationException("profile.dns", $"{path}: 无效的地址族或 DNS 模式。");
        if (platform != TargetPlatform.Linux && settings.MultiQueue == true)
            throw new ProfileValidationException("profile.route.multi_queue", $"{path}.multi_queue: 仅支持 Linux。");
        Duration(settings.ConnectTimeout, "profile.tuning.connect_timeout");
        Duration(settings.UrlTestInterval, "profile.tuning.url_test_interval");
        if (settings.TcpKeepAlive is { } keepAlive) Duration(keepAlive, "profile.tuning.tcp_keep_alive");
        if (settings.TcpKeepAliveInterval is { } interval) Duration(interval, "profile.tuning.tcp_keep_alive_interval");
        if (string.IsNullOrWhiteSpace(settings.TaildropDirectory) || settings.TaildropDirectory.Any(char.IsControl))
            throw new ProfileValidationException("profile.features.tailscale.taildrop_directory", $"{path}.taildrop_directory: 不能为空或包含控制字符。");
        var families = new HashSet<AddressFamily>();
        foreach (string address in settings.TunAddresses)
        {
            if (address is null) throw new ProfileValidationException("profile.route.tun_addresses", $"{path}.tun_addresses: 元素必须为 CIDR 字符串。");
            string[] parts = address.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.ToString() != parts[0]
                || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int prefix)
                || prefix < 1 || prefix > (ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)
                || ip.IsIPv4MappedToIPv6 || !families.Add(ip.AddressFamily))
                throw new ProfileValidationException("profile.route.tun_addresses", $"{path}.tun_addresses: 必须是规范且不重复地址族的 CIDR 地址。");
        }
        if (!families.SetEquals([AddressFamily.InterNetwork, AddressFamily.InterNetworkV6]))
            throw new ProfileValidationException("profile.route.tun_addresses", $"{path}.tun_addresses: 必须同时提供 IPv4 与 IPv6 捕获地址。");
        Duration(Route.SniffTimeout, "profile.route.sniff_timeout");
        Duration(Tuning.UrlTestIdleTimeout, "profile.tuning.url_test_idle_timeout");
        Duration(Dns.Timeout, "profile.dns.timeout");
        Duration(Dns.OptimisticTimeout, "profile.dns.optimistic_timeout");
        if (!Uri.TryCreate(Tuning.UrlTestUrl, UriKind.Absolute, out var probe) || probe.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(probe.Host) || !string.IsNullOrEmpty(probe.UserInfo) || !string.IsNullOrEmpty(probe.Fragment)
            || Tuning.UrlTestUrl.Any(char.IsWhiteSpace) || !probe.IsWellFormedOriginalString())
            throw new ProfileValidationException("profile.tuning.url_test_url", "profile.tuning.url_test_url: 必须是有效的 HTTP(S) URL，不含用户信息或片段。");
        if (Tuning.UrlTestTolerance is < 0 or > ushort.MaxValue)
            throw new ProfileValidationException("profile.tuning.url_test_tolerance", "profile: tolerance 必须在 0–65535 范围内。");
        if (Dns.CacheCapacity == 0)
            throw new ProfileValidationException("profile.dns.cache_capacity", "profile: 缓存容量必须为正。");
        if (Route.SniffExcludedPorts.Any(port => port is < 1 or > 65535)
            || Route.SniffExcludedPorts.Distinct().Count() != Route.SniffExcludedPorts.Length)
            throw new ProfileValidationException("profile.route.sniff_excluded_ports", "profile: 排除端口必须有效且唯一。");
    }

    private static void Duration(string value, string path)
    {
        if (!GoDurationHelper.IsPositiveDuration(value))
            throw new ProfileValidationException($"{path}", $"{path}: 必须为有效的正 Go duration，且不能超出 int64 纳秒范围。");
    }

    private static void ValidateId(string id, HashSet<string> ids, string section)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 32 || id[0] is < 'a' or > 'z'
            || id.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) || !ids.Add(id))
            throw new ProfileValidationException($"profile.{section}", $"profile.{section}: id 必须唯一，且为以小写字母开头的 1–32 位小写字母、数字或连字符。");
    }

    private static void ValidateRuleSetUrl(string url, string path)
    {
        string expanded = url.Replace("{tag}", "boxforge-tag", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsWhiteSpace) || url.Any(char.IsControl)
            || expanded.Contains('{') || expanded.Contains('}')
            || !Uri.TryCreate(expanded, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ProfileValidationException(path, "profile.rule_sets.url: 必须为绝对 HTTPS URL，不含空白、用户信息、片段或未知占位符。");
        int authorityEnd = url.IndexOfAny(['/', '?', '#'], url.IndexOf("://", StringComparison.Ordinal) + 3);
        if (url[..(authorityEnd < 0 ? url.Length : authorityEnd)].Contains("{tag}", StringComparison.Ordinal))
            throw new ProfileValidationException(path, "profile.rule_sets.url: {tag} 只能位于路径或查询部分。");
    }
}
