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
        return new PreparedConversion(yamlContent, nodes);
    }

    public string Convert(
        PreparedConversion prepared,
        TargetPlatform platform)
    {
        string generatedApiSecret = configSerializer
            .GetContentHash($"api-secret\0{prepared.SourceContent}\0{platform}")
            [..32];
        SingboxConfig config = configBuilder.Build(
            new SingboxBuildRequest(
                prepared.Nodes,
                platform,
                CacheId: null,
                GeneratedApiSecret: generatedApiSecret));

        string identityJson = configSerializer.Serialize(config);
        string cacheId = configSerializer.GetContentHash(identityJson);
        config = config with
        {
            Experimental = config.Experimental is null
                ? null
                : config.Experimental with
                {
                    CacheFile = config.Experimental.CacheFile is null
                        ? null
                        : config.Experimental.CacheFile with { CacheId = cacheId }
                }
        };

        configValidator.Validate(config);
        return configSerializer.Serialize(config);
    }
}

public sealed record PreparedConversion(
    string SourceContent,
    NodeCatalog Nodes);
