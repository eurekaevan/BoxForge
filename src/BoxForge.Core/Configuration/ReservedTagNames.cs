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
            SingboxTags.DirectOutbound,
            SingboxTags.BridgeOutbound,
            SingboxTags.TailscaleEndpoint,
            SingboxTags.TailscaleDns,
            SingboxTags.DirectAliDns,
            SingboxTags.DirectTencentDns,
            SingboxTags.ProxyGoogleDns,
            SingboxTags.ProxyCloudflareDns,
            SingboxTags.TunInbound,
            SingboxTags.MixedInbound,
            SingboxTags.ApiService,
            HttpClientTags.RuleSetProxy,
            HttpClientTags.DashboardDirect,
            DnsResponseTags.PriorityPrimary,
            DnsResponseTags.PrioritySecondary,
            DnsResponseTags.DomesticPrimary,
            DnsResponseTags.DomesticSecondary,
            DnsResponseTags.GlobalPrimary,
            DnsResponseTags.GlobalSecondary,
            RuleSetTags.Ads,
            RuleSetTags.Cn,
            RuleSetTags.CnIp
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
