using SubConvert.Builders;
using SubConvert.Models;
using SubConvert.Parsers;

namespace SubConvert.Services;

public class ConversionService(
    IClashParser clashParser,
    ISingboxConfigBuilder configBuilder,
    ISingboxConfigValidator configValidator,
    IConfigSerializer configSerializer)
{
    public ConversionResult Convert(
        string yamlContent,
        TargetPlatform platform,
        bool strictNodeValidation = false)
    {
        var clashConfig = clashParser.Parse(yamlContent)
            ?? throw new InvalidOperationException("YAML 解析失败，请检查文件内容。");

        string hashId = configSerializer.GetContentHash(yamlContent + platform);
        var config = configBuilder.Build(
            new SingboxBuildRequest(
                clashConfig,
                platform,
                hashId,
                strictNodeValidation));
        configValidator.Validate(config);
        string json = configSerializer.Serialize(config);

        return new ConversionResult(config, json);
    }
}
