using BoxForge.Models;

namespace BoxForge.Configuration;

public static class ReservedTagNames
{
    private static readonly HashSet<string> Tags = CreateTags();

    public static bool Contains(string tag) => Tags.Contains(tag);

    private static HashSet<string> CreateTags()
    {
        string[] fixedTags =
        [
            SingboxTags.MainProxyGroup,
            SingboxTags.AutoProxyGroup,
            SingboxTags.DirectOutbound,
            SingboxTags.BridgeOutbound,
            SingboxTags.TailscaleEndpoint,
            SingboxTags.TailscaleDns,
            SingboxTags.BootstrapDns,
            SingboxTags.NodeResolverDns,
            SingboxTags.LocalTencentDns,
            SingboxTags.LocalDns,
            SingboxTags.RemoteGoogleDns,
            SingboxTags.RemoteDns,
            SingboxTags.TunInbound,
            SingboxTags.MixedInbound,
            SingboxTags.ApiService,
            HttpClientTags.RuleSetDirect,
            DnsRaceTags.GoogleGoogle,
            DnsRaceTags.GoogleCloudflare,
            DnsRaceTags.ChinaTencent,
            DnsRaceTags.ChinaAliDns,
            DnsRaceTags.GlobalGoogle,
            DnsRaceTags.GlobalCloudflare,
            AdBlockingRuleSets.SagerAdsTag,
            "geosite-category-pt",
            "geosite-cn",
            "geoip-cn"
        ];

        var tags = new HashSet<string>(fixedTags, StringComparer.Ordinal);
        foreach (RegionDefinition region in ProfileDefinitions.Regions)
        {
            tags.Add(region.DisplayName);
            tags.Add($"{region.DisplayName} AUTO");
        }
        foreach (ServiceDefinition service in ProfileDefinitions.Services)
        {
            tags.Add(service.Name);
            tags.UnionWith(service.RuleSets);
        }

        return tags;
    }
}
