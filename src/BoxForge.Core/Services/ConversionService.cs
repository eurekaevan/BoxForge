using BoxForge.Builders;
using BoxForge.Configuration;
using BoxForge.Builders.Components;
using BoxForge.Models;
using BoxForge.Parsers;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Exceptions;

namespace BoxForge.Services;

public sealed class ConversionService(
    IClashParser clashParser,
    NodeCatalogBuilder nodeCatalogBuilder,
    IProxyCacheIdGenerator proxyCacheIdGenerator,
    ISingboxConfigBuilder configBuilder,
    ISingboxConfigValidator configValidator,
    IConfigSerializer configSerializer,
    BoxForgeProfile profile)
{
    public PreparedConversion Prepare(
        string yamlContent,
        bool strictNodeValidation = false)
    {
        ClashConfig clashConfig = clashParser.Parse(yamlContent)
            ?? throw new NodeParseException("YAML 解析失败，请检查文件内容。");
        NodeCatalog nodes = nodeCatalogBuilder.Build(
            clashConfig,
            strictNodeValidation);
        string cacheId = proxyCacheIdGenerator.Generate(clashConfig.Proxies);
        return new PreparedConversion(nodes, cacheId);
    }

    public string Convert(
        PreparedConversion prepared,
        TargetPlatform platform)
    {
        var request = new SingboxBuildRequest(
                prepared.Nodes,
                platform,
                prepared.CacheId);
        SingboxConfig config = configBuilder.Build(request);

        configValidator.Validate(config, profile.GetPlatform(platform).AddressFamily);
        return configSerializer.Serialize(config);
    }
}

public sealed record PreparedConversion(NodeCatalog Nodes, string CacheId);
