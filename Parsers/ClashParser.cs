using BoxForge.Models.Clash;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BoxForge.Parsers;

public interface IClashParser
{
    ClashConfig? Parse(string yamlContent);
}

public class ClashParser : IClashParser
{
    public ClashConfig? Parse(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var rawConfig = deserializer.Deserialize<RawClashConfig>(yamlContent);
        return rawConfig == null
            ? null
            : new ClashConfig
            {
                Proxies =
                [
                    .. rawConfig.Proxies.Select(proxy => new ClashProxyNode(proxy))
                ],
                DnsPolicies = ParseDnsPolicies(rawConfig.Dns?.NameserverPolicy)
            };
    }

    private static List<ClashDnsPolicy> ParseDnsPolicies(
        Dictionary<string, object>? policies)
    {
        if (policies == null)
        {
            return [];
        }

        var result = new List<ClashDnsPolicy>(policies.Count);
        foreach ((string pattern, object value) in policies)
        {
            IEnumerable<object?> values = value is System.Collections.IEnumerable
                and not string
                ? ((System.Collections.IEnumerable)value).Cast<object?>()
                : [value];
            string[] servers =
            [
                .. values
                    .Select(item => item?.ToString()?.Trim())
                    .Where(item => !string.IsNullOrEmpty(item))
                    .Select(item => item!)
            ];
            if (!string.IsNullOrWhiteSpace(pattern) && servers.Length > 0)
            {
                result.Add(new ClashDnsPolicy(pattern, servers));
            }
        }

        return result;
    }

    private sealed record RawClashConfig
    {
        [YamlMember(Alias = "proxies")]
        public List<Dictionary<string, object>> Proxies { get; init; } = [];

        [YamlMember(Alias = "dns")]
        public RawDnsConfig? Dns { get; init; }
    }

    private sealed record RawDnsConfig
    {
        [YamlMember(Alias = "nameserver-policy")]
        public Dictionary<string, object> NameserverPolicy { get; init; } = [];
    }
}
