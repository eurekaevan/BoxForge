using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;

namespace BoxForge.Models;

public abstract record NodeConversionResult
{
    public static ConvertedNode Success(ProxyOutbound outbound) =>
        new(outbound);

    public static InvalidNode Fail(string errorMessage) =>
        new(errorMessage);
}

public sealed record ConvertedNode(ProxyOutbound Outbound) : NodeConversionResult;

public sealed record InvalidNode(string ErrorMessage) : NodeConversionResult;

internal sealed record ParsedNode(ProxyNode Node) : NodeConversionResult;
