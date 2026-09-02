using BoxForge.Models.Singbox;

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
