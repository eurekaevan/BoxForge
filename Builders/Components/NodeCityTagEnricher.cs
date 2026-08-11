using System.Net;
using System.Net.Sockets;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Logging;

namespace BoxForge.Builders.Components;

public interface IHostAddressResolver
{
    IReadOnlyList<IPAddress> Resolve(string hostName);
}

public sealed class HostAddressResolver : IHostAddressResolver
{
    public IReadOnlyList<IPAddress> Resolve(string hostName) =>
        Dns.GetHostAddresses(hostName)
            .Where(address => address.AddressFamily is
                AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct()
            .OrderBy(address => address.AddressFamily)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
}

public sealed partial class NodeCityTagEnricher(
    IHostAddressResolver addressResolver,
    ICityDatabase cityDatabase,
    ILogger<NodeCityTagEnricher> logger)
{
    public NodeCatalog Enrich(NodeCatalog nodes)
    {
        var outbounds = new List<ProxyOutbound>(nodes.Outbounds.Count);
        var names = new List<string>(nodes.Names.Count);

        foreach (var outbound in nodes.Outbounds)
        {
            string tag = string.IsNullOrEmpty(outbound.Tag)
                ? outbound.Tag
                : TryBuildAnnotatedTag(outbound.Tag, outbound.Server);
            outbounds.Add(outbound with { Tag = tag });
            if (!string.IsNullOrEmpty(tag))
            {
                names.Add(tag);
            }
        }

        return nodes with
        {
            Outbounds = outbounds,
            Names = names
        };
    }

    private string TryBuildAnnotatedTag(string originalTag, string server)
    {
        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(server, out var serverAddress))
        {
            addresses = [serverAddress];
        }
        else
        {
            try
            {
                addresses = addressResolver.Resolve(server);
            }
            catch (Exception ex)
            {
                LogDnsFailure(logger, ex, originalTag, server);
                return originalTag;
            }

            if (addresses.Count == 0)
            {
                LogDnsNoAddresses(logger, originalTag, server);
                return originalTag;
            }
        }

        var cities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            string? city;
            try
            {
                city = cityDatabase.FindEnglishCity(address);
            }
            catch (Exception ex)
            {
                LogDatabaseFailure(logger, ex, originalTag, server, address);
                return originalTag;
            }

            if (string.IsNullOrWhiteSpace(city))
            {
                LogMissingCity(logger, originalTag, server, address);
                return originalTag;
            }

            cities.Add(city.Trim());
        }

        return cities.Count == 0
            ? originalTag
            : $"{originalTag} | {string.Join('/', cities)}";
    }

    [LoggerMessage(
        1,
        LogLevel.Warning,
        "节点城市标注 DNS 解析失败，保留原 tag：{Tag}，server={Server}")]
    private static partial void LogDnsFailure(
        ILogger logger,
        Exception exception,
        string tag,
        string server);

    [LoggerMessage(
        2,
        LogLevel.Warning,
        "节点城市标注 DNS 未返回 A/AAAA 地址，保留原 tag：{Tag}，server={Server}")]
    private static partial void LogDnsNoAddresses(
        ILogger logger,
        string tag,
        string server);

    [LoggerMessage(
        3,
        LogLevel.Warning,
        "节点城市标注数据库准备或查询失败，保留原 tag：{Tag}，server={Server}，address={Address}")]
    private static partial void LogDatabaseFailure(
        ILogger logger,
        Exception exception,
        string tag,
        string server,
        IPAddress address);

    [LoggerMessage(
        4,
        LogLevel.Warning,
        "节点城市标注缺少英文城市名，保留原 tag：{Tag}，server={Server}，address={Address}")]
    private static partial void LogMissingCity(
        ILogger logger,
        string tag,
        string server,
        IPAddress address);
}
