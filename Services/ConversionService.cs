using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Models;
using BoxForge.Parsers;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;

namespace BoxForge.Services;

public class ConversionService(
    IClashParser clashParser,
    NodeCatalogBuilder nodeCatalogBuilder,
    NodeCityTagEnricher nodeCityTagEnricher,
    IProxyCacheIdGenerator proxyCacheIdGenerator,
    ISingboxConfigBuilder configBuilder,
    ISingboxConfigValidator configValidator,
    IConfigSerializer configSerializer)
{
    public PreparedConversion Prepare(
        string yamlContent,
        bool strictNodeValidation = false)
    {
        ClashConfig clashConfig = clashParser.Parse(yamlContent)
            ?? throw new InvalidOperationException("YAML 解析失败，请检查文件内容。");
        NodeCatalog nodes = nodeCatalogBuilder.Build(
            clashConfig,
            strictNodeValidation);
        nodes = nodeCityTagEnricher.Enrich(nodes);
        string cacheId = proxyCacheIdGenerator.Generate(clashConfig.Proxies);
        return new PreparedConversion(nodes, cacheId);
    }

    public string Convert(
        PreparedConversion prepared,
        TargetPlatform platform)
    {
        SingboxConfig config = configBuilder.Build(
            new SingboxBuildRequest(
                prepared.Nodes,
                platform,
                prepared.CacheId));

        configValidator.Validate(config);
        return configSerializer.Serialize(config);
    }
}

public sealed record PreparedConversion(NodeCatalog Nodes, string CacheId);
