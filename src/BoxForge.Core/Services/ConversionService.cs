using BoxForge.Builders;
using System.Text.Json.Nodes;
using BoxForge.Configuration;
using BoxForge.Builders.Components;
using BoxForge.Models;
using BoxForge.Parsers;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Exceptions;

namespace BoxForge.Services;

internal sealed class ConversionService(
    IClashParser clashParser,
    NodeCatalogBuilder nodeCatalogBuilder,
    IProxyCacheIdGenerator proxyCacheIdGenerator,
    ISingboxConfigBuilder configBuilder,
    ISingboxConfigValidator configValidator,
    IConfigSerializer configSerializer,
    BoxForgeProfile profile)
{
    internal BoxForgeProfile ResolveProfile(TargetPlatform platform, JsonObject? overlay) => profile.ForPlatform(platform, overlay);

    public PreparedConversion Prepare(
        string yamlContent,
        BoxForgeProfile effectiveProfile)
    {
        ClashConfig clashConfig = clashParser.Parse(yamlContent)
            ?? throw new NodeParseException("YAML 解析失败，请检查文件内容。");
        NodeCatalog nodes = nodeCatalogBuilder.Build(
            clashConfig,
            effectiveProfile);
        string cacheId = proxyCacheIdGenerator.Generate(clashConfig.Proxies);
        return new PreparedConversion(nodes, cacheId);
    }

    public string Convert(
        PreparedConversion prepared,
        TargetPlatform platform,
        BoxForgeProfile effectiveProfile)
    {
        var request = new SingboxBuildRequest(
                prepared.Nodes,
                platform,
                prepared.CacheId)
        { Profile = effectiveProfile };
        SingboxConfig config = configBuilder.Build(request);

        configValidator.Validate(config, effectiveProfile.GetPlatform(platform).AddressFamily);
        return configSerializer.Serialize(config);
    }
}

internal sealed record PreparedConversion(NodeCatalog Nodes, string CacheId);
