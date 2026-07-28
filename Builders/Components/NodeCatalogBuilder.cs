using System.Net;
using Microsoft.Extensions.Logging;
using SubConvert.Converters;
using SubConvert.Models;
using SubConvert.Models.Clash;
using SubConvert.Models.Singbox;

namespace SubConvert.Builders.Components;

public class NodeCatalogBuilder(
    IEnumerable<IProxyConverter> converters,
    ILogger<NodeCatalogBuilder> logger)
{
    public NodeCatalog Build(ClashConfig clashConfig)
    {
        var outbounds = new List<ProxyOutbound>();
        var names = new List<string>();
        var serverDomains = new HashSet<string>();

        foreach (var proxy in clashConfig.Proxies)
        {
            if (proxy.Type == null)
            {
                continue;
            }

            var converter = converters.FirstOrDefault(
                candidate => candidate.CanHandle(proxy.Type));
            if (converter == null)
            {
                continue;
            }

            var result = converter.Convert(proxy);
            if (result is InvalidNode invalidNode)
            {
                logger.LogWarning("跳过无效节点 -> {ErrorMessage}", invalidNode.ErrorMessage);
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

        return new NodeCatalog(outbounds, names, [.. serverDomains]);
    }
}
