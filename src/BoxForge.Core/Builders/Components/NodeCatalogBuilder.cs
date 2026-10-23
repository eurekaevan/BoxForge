using BoxForge.Converters;
using BoxForge.Exceptions;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal static class NodeCatalogBuilder
{
    internal static NodeCatalog Build(
        ClashConfig clashConfig)
    {
        var nodes = new List<ProxyNode>();
        var names = new List<string>();

        for (int index = 0; index < clashConfig.Proxies.Count; index++)
        {
            ProxyNode node;
            try
            {
                node = ProxyNodeParser.Parse(clashConfig.Proxies[index]);
            }
            catch (NodeParseException ex)
            {
                throw ex.AtPath($"source.proxies[{index}]");
            }
            string tag = node.Name;
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
