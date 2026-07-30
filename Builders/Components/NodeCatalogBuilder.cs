using System.Net;
using Microsoft.Extensions.Logging;
using SubConvert.Converters;
using SubConvert.Exceptions;
using SubConvert.Models;
using SubConvert.Models.Clash;
using SubConvert.Models.Singbox;

namespace SubConvert.Builders.Components;

public class NodeCatalogBuilder(
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
                logger.LogWarning("跳过无效节点 -> {ErrorMessage}", invalidNode.ErrorMessage);
                if (strictNodeValidation)
                {
                    throw new NodeParseException(invalidNode.ErrorMessage);
                }

                continue;
            }

            var outbound = ((ConvertedNode)result).Outbound;
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
}
