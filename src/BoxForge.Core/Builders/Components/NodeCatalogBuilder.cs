using BoxForge.Configuration;
using BoxForge.Converters;
using BoxForge.Exceptions;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal sealed class NodeCatalogBuilder(BoxForgeProfile profile)
{
    internal NodeCatalog Build(
        ClashConfig clashConfig,
        BoxForgeProfile? effectiveProfile = null)
    {
        var settings = effectiveProfile ?? profile;
        var nodes = new List<ProxyNode>();
        var names = new List<string>();

        foreach (var proxy in clashConfig.Proxies)
        {
            ProxyNode node = ProxyNodeParser.Parse(proxy);
            string tag = node.Name;
            if (settings.IsReservedTag(tag))
            {
                throw new NodeParseException($"节点名称 '{tag}' 与 BoxForge 保留 tag 冲突");
            }

            names.Add(tag);
            nodes.Add(node);
        }

        if (nodes.Count == 0)
        {
            throw new NodeParseException("配置中没有可转换的有效节点");
        }

        return new NodeCatalog(nodes.AsReadOnly(), names.AsReadOnly());
    }
}
