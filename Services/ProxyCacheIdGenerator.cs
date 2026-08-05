using System.Collections;
using System.Text.Json;
using BoxForge.Models.Clash;

namespace BoxForge.Services;

public interface IProxyCacheIdGenerator
{
    string Generate(IReadOnlyList<ClashProxyNode> proxies);
}

public sealed class ProxyCacheIdGenerator(
    IConfigSerializer configSerializer) : IProxyCacheIdGenerator
{
    public string Generate(IReadOnlyList<ClashProxyNode> proxies)
    {
        object?[] canonicalProxies =
        [
            .. proxies.Select(proxy => Canonicalize(proxy))
        ];
        string identityJson = JsonSerializer.Serialize(canonicalProxies);
        return configSerializer.GetContentHash(identityJson);
    }

    private static object? Canonicalize(object? value)
    {
        if (value is ClashObject clashObject)
        {
            return clashObject.Properties
                .OrderBy(property => property.Key, StringComparer.Ordinal)
                .ToDictionary(
                    property => property.Key,
                    property => Canonicalize(property.Value),
                    StringComparer.Ordinal);
        }

        if (value is IEnumerable sequence and not string)
        {
            return sequence
                .Cast<object?>()
                .Select(Canonicalize)
                .ToArray();
        }

        return value;
    }
}
