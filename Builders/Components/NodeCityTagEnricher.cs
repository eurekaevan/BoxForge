using System.Net;
using System.Text.RegularExpressions;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public sealed partial class NodeCityTagEnricher(
    IOptions<NodeEnrichmentOptions> options,
    IExitIpDetector exitIpDetector,
    IDbIpCityDatabase dbIpCityDatabase,
    IIp2LocationCityClient ip2LocationCityClient,
    ILogger<NodeCityTagEnricher> logger)
{
    private readonly NodeEnrichmentOptions enrichmentOptions = options.Value;
    private readonly Dictionary<IPAddress, CityLookupResult> cityCache = [];
    private readonly DbIpLookupState dbIpState = new();

    public async Task<NodeCatalog> EnrichAsync(
        NodeCatalog nodes,
        CancellationToken cancellationToken = default)
    {
        if (!enrichmentOptions.Enabled)
        {
            return nodes;
        }

        var outbounds = new List<ProxyOutbound>(nodes.Outbounds.Count);
        var names = new List<string>(nodes.Names.Count);

        foreach (ProxyOutbound outbound in nodes.Outbounds)
        {
            IPAddress? exitAddress = await exitIpDetector.DetectAsync(
                outbound,
                cancellationToken);
            string tag = outbound.Tag;
            if (exitAddress != null)
            {
                if (!cityCache.TryGetValue(exitAddress, out var cities))
                {
                    cities = await LookupCitiesAsync(
                        exitAddress,
                        dbIpState,
                        cancellationToken);
                    cityCache.Add(exitAddress, cities);
                }

                tag = BuildEnrichedTag(outbound.Tag, cities);
            }

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

    private async Task<CityLookupResult> LookupCitiesAsync(
        IPAddress exitAddress,
        DbIpLookupState dbIpState,
        CancellationToken cancellationToken)
    {
        if (!dbIpState.InitializationAttempted)
        {
            dbIpState.InitializationAttempted = true;
            try
            {
                await dbIpCityDatabase.InitializeAsync(cancellationToken);
                dbIpState.Available = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                LogDbIpInitializationFailure(logger);
            }
        }

        string? dbIpCity = null;
        if (dbIpState.Available)
        {
            try
            {
                dbIpCity = NormalizeDbIpCity(
                    dbIpCityDatabase.FindEnglishCity(exitAddress));
            }
            catch (Exception)
            {
                LogDbIpFailure(logger, exitAddress);
            }
        }

        string? ip2LocationCity = null;
        try
        {
            ip2LocationCity = NormalizeCity(
                await ip2LocationCityClient.FindCityAsync(
                    exitAddress,
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            LogIp2LocationFailure(logger, exitAddress);
        }

        return new CityLookupResult(dbIpCity, ip2LocationCity);
    }

    private static string BuildEnrichedTag(
        string originalTag,
        CityLookupResult cities)
    {
        if (string.IsNullOrEmpty(originalTag))
        {
            return originalTag;
        }

        string? citySuffix = MergeCities(
            cities.DbIpCity,
            cities.Ip2LocationCity);
        return citySuffix == null
            ? originalTag
            : $"{originalTag}>{citySuffix}";
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
        "DB-IP City Lite 城市查询失败，exit_address={Address}")]
    private static partial void LogDbIpFailure(
        ILogger logger,
        IPAddress address);

    [LoggerMessage(
        2,
        LogLevel.Warning,
        "IP2Location.io 城市查询失败，exit_address={Address}")]
    private static partial void LogIp2LocationFailure(
        ILogger logger,
        IPAddress address);

    [LoggerMessage(
        3,
        LogLevel.Warning,
        "DB-IP City Lite 数据库初始化失败，仅使用 IP2Location.io。")]
    private static partial void LogDbIpInitializationFailure(ILogger logger);

    private sealed record CityLookupResult(
        string? DbIpCity,
        string? Ip2LocationCity);

    private sealed class DbIpLookupState
    {
        public bool InitializationAttempted { get; set; }
        public bool Available { get; set; }
    }
}
