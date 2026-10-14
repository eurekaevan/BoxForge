using BoxForge.Models.Clash;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BoxForge.Parsers;

public interface IClashParser
{
    ClashConfig? Parse(string yamlContent);
}

public sealed class ClashParser : IClashParser
{
    public ClashConfig? Parse(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .WithMaximumRecursion(ClashObject.MaximumDepth)
            .IgnoreUnmatchedProperties()
            .Build();

        var rawConfig = deserializer.Deserialize<RawClashConfig>(yamlContent);
        if (rawConfig == null) return null;
        var budget = new ClashObject.NormalizationBudget();
        var proxies = new List<ClashProxyNode>(rawConfig.Proxies.Count);
        foreach (Dictionary<string, object> proxy in rawConfig.Proxies)
            proxies.Add(new ClashProxyNode(proxy, budget));
        return new ClashConfig { Proxies = proxies };
    }

    private sealed record RawClashConfig
    {
        [YamlMember(Alias = "proxies")]
        public List<Dictionary<string, object>> Proxies { get; init; } = [];
    }
}
