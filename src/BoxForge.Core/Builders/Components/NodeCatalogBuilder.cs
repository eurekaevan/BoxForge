using Microsoft.Extensions.Logging;
using BoxForge.Configuration;
using BoxForge.Converters;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Clash;

namespace BoxForge.Builders.Components;

public sealed partial class NodeCatalogBuilder(
    IEnumerable<IProxyConverter> converters,
    ILogger<NodeCatalogBuilder> logger,
    BoxForgeProfile profile)
{
    public NodeCatalog Build(
        ClashConfig clashConfig,
        bool strictNodeValidation = false)
    {
        var entries = new List<NodeConversionResult>();
        var names = new List<string>();

        foreach (var proxy in clashConfig.Proxies)
        {
            if (proxy.Type == null)
            {
                string message = $"节点 '{proxy.Name}' 缺少 type 字段";
                ReportInvalidNode(message);
                continue;
            }

            var converter = converters.FirstOrDefault(
                candidate => candidate.CanHandle(proxy.Type));
            if (converter == null)
            {
                string message = $"节点 '{proxy.Name}' 使用了不支持的类型: {proxy.Type}";
                ReportInvalidNode(message);
                continue;
            }

            var result = converter is INodeFrontend frontend
                ? frontend.Parse(proxy)
                : converter.Convert(proxy);
            if (result is InvalidNode invalidNode)
            {
                ReportInvalidNode(invalidNode.ErrorMessage);
                continue;
            }

            string tag = result switch
            {
                ParsedNode parsed => parsed.Node.Name,
                ConvertedNode converted => converted.Outbound.Tag,
                _ => throw new InvalidOperationException("Unexpected node conversion result.")
            };
            if (profile.IsReservedTag(tag))
            {
                string message =
                    $"节点名称 '{tag}' 与 BoxForge 保留 tag 冲突";
                ReportInvalidNode(message);
                continue;
            }

            if (!string.IsNullOrEmpty(tag))
            {
                names.Add(tag);
            }

            entries.Add(result);
        }

        if (strictNodeValidation && entries.Count == 0)
        {
            throw new NodeParseException("配置中没有可转换的有效节点");
        }

        return NodeCatalog.FromEntries(entries.AsReadOnly(), names.AsReadOnly());

        void ReportInvalidNode(string message)
        {
            if (strictNodeValidation)
            {
                throw new NodeParseException(message);
            }

            LogInvalidNode(logger, message);
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "跳过无效节点 -> {ErrorMessage}")]
    private static partial void LogInvalidNode(ILogger logger, string errorMessage);
}
