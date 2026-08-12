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
                ]
            };
    }

    private sealed record RawClashConfig
    {
        [YamlMember(Alias = "proxies")]
        public List<Dictionary<string, object>> Proxies { get; init; } = [];

    }
}
