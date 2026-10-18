using BoxForge.Models.Clash;
using BoxForge.Exceptions;
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
        if (rawConfig.Proxies == null)
            throw new NodeParseException("proxies 必须是节点列表，不能为 null。");
        var budget = new ClashObject.NormalizationBudget();
        var proxies = new List<ClashProxyNode>(rawConfig.Proxies.Count);
        for (int index = 0; index < rawConfig.Proxies.Count; index++)
        {
            Dictionary<string, object>? proxy = rawConfig.Proxies[index];
            if (proxy == null)
                throw new NodeParseException($"proxies[{index}] 必须是节点对象，不能为 null。");
            proxies.Add(new ClashProxyNode(proxy, budget));
        }
        return new ClashConfig { Proxies = proxies };
    }

    private sealed record RawClashConfig
    {
        [YamlMember(Alias = "proxies")]
        public List<Dictionary<string, object>?>? Proxies { get; init; } = [];
    }
}
