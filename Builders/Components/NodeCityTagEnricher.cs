using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public interface IHostAddressResolver
{
    IReadOnlyList<IPAddress> Resolve(string hostName);

    Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string hostName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Resolve(hostName));
    }
}

public sealed class HostAddressResolver : IHostAddressResolver
{
    public IReadOnlyList<IPAddress> Resolve(string hostName) =>
        [.. Dns.GetHostAddresses(hostName)
            .Where(IsInternetAddress)
            .Distinct()
            .OrderBy(address => address.AddressFamily)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)];

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string hostName,
        CancellationToken cancellationToken = default) =>
        [.. (await Dns.GetHostAddressesAsync(hostName, cancellationToken))
            .Where(IsInternetAddress)
            .Distinct()
            .OrderBy(address => address.AddressFamily)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)];

    private static bool IsInternetAddress(IPAddress address) =>
        address.AddressFamily is
            AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
}

public sealed partial class NodeCityTagEnricher(
    IOptions<NodeEnrichmentOptions> options,
    IHostAddressResolver addressResolver,
    IDbIpCityDatabase dbIpCityDatabase,
    IIp2LocationCityClient ip2LocationCityClient,
    ILogger<NodeCityTagEnricher> logger)
{
    private readonly NodeEnrichmentOptions enrichmentOptions = options.Value;

    public async Task<NodeCatalog> EnrichAsync(
        NodeCatalog nodes,
        CancellationToken cancellationToken = default)
    {
        if (!enrichmentOptions.Enabled)
        {
            return nodes;
        }

        var addressesByServer = await ResolveServersAsync(
            nodes.Outbounds,
            cancellationToken);
        IPAddress[] uniqueAddresses = [.. addressesByServer.Values
            .SelectMany(addresses => addresses)
            .Distinct()
            .OrderBy(address => address.AddressFamily)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)];

        Task<Dictionary<IPAddress, string?>> dbIpLookup =
            LookupDbIpCitiesAsync(uniqueAddresses, cancellationToken);
        Task<Dictionary<IPAddress, string?>> ip2LocationLookup =
            LookupIp2LocationCitiesAsync(uniqueAddresses, cancellationToken);
        await Task.WhenAll(dbIpLookup, ip2LocationLookup);
        var dbIpCities = await dbIpLookup;
        var ip2LocationCities = await ip2LocationLookup;

        var outbounds = new List<ProxyOutbound>(nodes.Outbounds.Count);
        var names = new List<string>(nodes.Names.Count);
        foreach (var outbound in nodes.Outbounds)
        {
            string tag = BuildEnrichedTag(
                outbound.Tag,
                outbound.Server,
                addressesByServer,
                dbIpCities,
                ip2LocationCities);
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

    private async Task<Dictionary<string, IReadOnlyList<IPAddress>>>
        ResolveServersAsync(
            IReadOnlyList<ProxyOutbound> outbounds,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<IPAddress>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var outbound in outbounds)
        {
            string server = outbound.Server;
            if (string.IsNullOrWhiteSpace(server) || result.ContainsKey(server))
            {
                continue;
            }

            if (IPAddress.TryParse(server, out var serverAddress))
            {
                result[server] = [serverAddress];
                continue;
            }

            try
            {
                IReadOnlyList<IPAddress> addresses = await addressResolver.ResolveAsync(
                    server,
                    cancellationToken);
                result[server] = [.. addresses
                    .Where(address => address.AddressFamily is
                        AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Distinct()
                    .OrderBy(address => address.AddressFamily)
                    .ThenBy(address => address.ToString(), StringComparer.Ordinal)];
                if (result[server].Count == 0)
                {
                    LogDnsNoAddresses(logger, outbound.Tag, server);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result[server] = [];
                LogDnsFailure(logger, ex, outbound.Tag, server);
            }
        }

        return result;
    }

    private async Task<Dictionary<IPAddress, string?>> LookupDbIpCitiesAsync(
        IPAddress[] addresses,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<IPAddress, string?>();
        if (addresses.Length == 0)
        {
            return result;
        }

        try
        {
            await dbIpCityDatabase.InitializeAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDbIpInitializationFailure(logger, ex);
            return result;
        }

        foreach (IPAddress address in addresses)
        {
            try
            {
                result[address] = NormalizeDbIpCity(
                    dbIpCityDatabase.FindEnglishCity(address));
            }
            catch (Exception ex)
            {
                result[address] = null;
                LogDbIpFailure(logger, ex, address);
            }
        }

        return result;
    }

    private async Task<Dictionary<IPAddress, string?>>
        LookupIp2LocationCitiesAsync(
            IReadOnlyList<IPAddress> addresses,
            CancellationToken cancellationToken)
    {
        Task<(IPAddress Address, string? City)>[] tasks = [.. addresses.Select(
            address => LookupIp2LocationCityAsync(address, cancellationToken))];
        var result = await Task.WhenAll(tasks);
        return result.ToDictionary(item => item.Address, item => item.City);
    }

    private async Task<(IPAddress Address, string? City)>
        LookupIp2LocationCityAsync(
            IPAddress address,
            CancellationToken cancellationToken)
    {
        try
        {
            string? city = await ip2LocationCityClient.FindCityAsync(
                address,
                cancellationToken);
            return (address, NormalizeCity(city));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            LogIp2LocationFailure(logger, address);
            return (address, null);
        }
    }

    private static string BuildEnrichedTag(
        string originalTag,
        string server,
        Dictionary<string, IReadOnlyList<IPAddress>> addressesByServer,
        IReadOnlyDictionary<IPAddress, string?> dbIpCities,
        IReadOnlyDictionary<IPAddress, string?> ip2LocationCities)
    {
        if (string.IsNullOrEmpty(originalTag)
            || !addressesByServer.TryGetValue(server, out var addresses))
        {
            return originalTag;
        }

        string? dbIpCity = JoinDistinctCities(addresses, dbIpCities);
        string? ip2LocationCity = JoinDistinctCities(
            addresses,
            ip2LocationCities);
        string? citySuffix = MergeCities(dbIpCity, ip2LocationCity);
        return citySuffix == null
            ? originalTag
            : $"{originalTag}>{citySuffix}";
    }

    private static string? JoinDistinctCities(
        IReadOnlyList<IPAddress> addresses,
        IReadOnlyDictionary<IPAddress, string?> cities)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinctCities = new List<string>();
        foreach (IPAddress address in addresses)
        {
            if (cities.TryGetValue(address, out string? city)
                && city != null
                && seen.Add(city))
            {
                distinctCities.Add(city);
            }
        }

        return distinctCities.Count == 0
            ? null
            : string.Join('/', distinctCities);
    }

    private static string? MergeCities(
        string? dbIpCity,
        string? ip2LocationCity)
    {
        if (dbIpCity == null)
        {
            return ip2LocationCity;
        }

        if (ip2LocationCity == null
            || string.Equals(
                dbIpCity,
                ip2LocationCity,
                StringComparison.OrdinalIgnoreCase))
        {
            return dbIpCity;
        }

        return $"{dbIpCity}\\{ip2LocationCity}";
    }

    private static string? NormalizeDbIpCity(string? city)
    {
        string? normalized = NormalizeCity(city);
        if (normalized == null)
        {
            return null;
        }

        return NormalizeCity(TrailingParenthesesRegex().Replace(normalized, ""));
    }

    private static string? NormalizeCity(string? city)
    {
        string? normalized = city?.Trim();
        return string.IsNullOrEmpty(normalized) || normalized == "-"
            ? null
            : normalized;
    }

    [GeneratedRegex(@"(?:\s*(?:\([^()]*\)|（[^（）]*）))+\s*$")]
    private static partial Regex TrailingParenthesesRegex();

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
        "DB-IP City Lite 城市查询失败，address={Address}")]
    private static partial void LogDbIpFailure(
        ILogger logger,
        Exception exception,
        IPAddress address);

    [LoggerMessage(
        4,
        LogLevel.Warning,
        "IP2Location.io 城市查询失败，address={Address}")]
    private static partial void LogIp2LocationFailure(
        ILogger logger,
        IPAddress address);

    [LoggerMessage(
        5,
        LogLevel.Warning,
        "DB-IP City Lite 数据库初始化失败，保留原 tag。")]
    private static partial void LogDbIpInitializationFailure(
        ILogger logger,
        Exception exception);
}
