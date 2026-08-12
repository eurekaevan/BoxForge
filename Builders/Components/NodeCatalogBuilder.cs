using System.Net;
using Microsoft.Extensions.Logging;
using BoxForge.Converters;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public partial class NodeCatalogBuilder(
    IEnumerable<IProxyConverter> converters,
    ILogger<NodeCatalogBuilder> logger)
{
    public NodeCatalog Build(
        ClashConfig clashConfig,
        bool strictNodeValidation = false)
    {
        var outbounds = new List<ProxyOutbound>();
        var names = new List<string>();
        var serverDomains = new HashSet<string>();

        foreach (var proxy in clashConfig.Proxies)
        {
            if (proxy.Type == null)
            {
                if (strictNodeValidation)
                {
                    throw new NodeParseException(
                        $"节点 '{proxy.Name}' 缺少 type 字段");
                }

                continue;
            }

            var converter = converters.FirstOrDefault(
                candidate => candidate.CanHandle(proxy.Type));
            if (converter == null)
            {
                if (strictNodeValidation)
                {
                    throw new NodeParseException(
                        $"节点 '{proxy.Name}' 使用了不支持的类型: {proxy.Type}");
                }

                continue;
            }

            var result = converter.Convert(proxy);
            if (result is InvalidNode invalidNode)
            {
                LogInvalidNode(logger, invalidNode.ErrorMessage);
                if (strictNodeValidation)
                {
                    throw new NodeParseException(invalidNode.ErrorMessage);
                }

                continue;
            }

            ProxyOutbound convertedOutbound = ((ConvertedNode)result).Outbound;
            var outbound = convertedOutbound with
            {
                ProbeDnsServers = clashConfig.FindNodeDnsServers(
                    convertedOutbound.Server)
            };
            if (!string.IsNullOrEmpty(outbound.Tag))
            {
                names.Add(outbound.Tag);
            }

            if (!string.IsNullOrEmpty(outbound.Server)
                && !IPAddress.TryParse(outbound.Server, out _))
            {
                serverDomains.Add(outbound.Server);
            }

            outbounds.Add(outbound);
        }

        if (strictNodeValidation && outbounds.Count == 0)
        {
            throw new NodeParseException("配置中没有可转换的有效节点");
        }

        return new NodeCatalog(
            outbounds,
            names,
            [.. serverDomains.Order(StringComparer.Ordinal)]);
    }

    [LoggerMessage(1, LogLevel.Warning, "跳过无效节点 -> {ErrorMessage}")]
    private static partial void LogInvalidNode(ILogger logger, string errorMessage);
}
