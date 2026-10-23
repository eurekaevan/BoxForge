using System.Collections.Immutable;
using BoxForge.Models;

namespace BoxForge.Exceptions;

public class BoxForgeConversionException(
    string message,
    Exception innerException)
    : Exception(message, innerException)
{
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = innerException switch
    {
        NodeParseException source => source.Diagnostics,
        ConfigValidationException config => config.Diagnostics,
        ProfileValidationException profile => profile.Diagnostics,
        YamlDotNet.Core.YamlException => ImmutableArray.Create(new ConfigDiagnostic("SRC001", "source", "来源 YAML 语法或结构无效。")),
        System.Text.RegularExpressions.RegexMatchTimeoutException => ImmutableArray.Create(new ConfigDiagnostic("IR008", "plan.groups.regions", "地区匹配超过允许时限。")),
        _ => ImmutableArray<ConfigDiagnostic>.Empty
    };
}
