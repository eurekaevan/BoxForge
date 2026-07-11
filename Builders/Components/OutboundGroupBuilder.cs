using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubConvert.Configuration;
using SubConvert.Converters;
using SubConvert.Models.Singbox;

namespace SubConvert.Builders.Components;

// 注入 IOptions 和 ILogger
public class OutboundGroupBuilder(
    IEnumerable<IProxyConverter> converters, 
    IOptions<AppSettings> options, 
    ILogger<OutboundGroupBuilder> logger) : IConfigComponentBuilder
{
    private readonly AppSettings _appSettings = options.Value;

    public void Build(BuildContext ctx)
    {
        BuildDirectOutbound(ctx);
        BuildProxyNodes(ctx); 
        BuildRegionOutbounds(ctx);
        BuildProxyGroups(ctx);
    }

    private void BuildDirectOutbound(BuildContext ctx)
    {
        ctx.DirectOutbound = new DirectOutbound { Tag = _appSettings.Direct, DomainResolver = "local" };
    }

    private void BuildProxyNodes(BuildContext ctx)
    {
        if (ctx.RawClashConfig?.Proxies == null) return;
        foreach (var p in ctx.RawClashConfig.Proxies)
        {
            if (!p.TryGetValue("type", out var typeObj)) continue;
            var converter = converters.FirstOrDefault(c => c.CanHandle(typeObj.ToString()!));
            if (converter == null) continue;

            var result = converter.Convert(p);
            if (!result.IsSuccess)
            {
                logger.LogWarning("跳过无效节点 -> {ErrorMessage}", result.ErrorMessage);
                continue;
            }

            ProxyOutbound outbound = result.Outbound!;
            if (!string.IsNullOrEmpty(outbound.Tag)) ctx.AllNodeNames.Add(outbound.Tag);
            if (!string.IsNullOrEmpty(outbound.Server) && !IPAddress.TryParse(outbound.Server, out _))
                ctx.ProxyServerDomains.Add(outbound.Server);
            ctx.NodeOutbounds.Add(outbound);
        }
    }

    private void BuildRegionOutbounds(BuildContext ctx)
    {
        foreach (var kvp in ProfileDefinitions.Regions)
        {
            var matchedNodes = ctx.AllNodeNames.Where(name => kvp.Value.Pattern.IsMatch(name)).ToList();
            if (matchedNodes.Count >= 2)
            {
                ctx.GeneratedRegions[kvp.Key] = kvp.Value.DisplayName;
                ctx.RegionOutbounds.Add(new SelectorOutbound
                {
                    Tag = kvp.Value.DisplayName,
                    Outbounds = matchedNodes,
                    Default = matchedNodes.FirstOrDefault(),
                    InterruptExistConnections = true
                });
            }
        }
    }

    private void BuildProxyGroups(BuildContext ctx)
    {
        var mainGroupOptions = new List<string>(ctx.GeneratedRegions.Values);
        mainGroupOptions.AddRange(ctx.AllNodeNames);
        mainGroupOptions.Add(_appSettings.Direct);

        ctx.MainOutbounds.Add(new SelectorOutbound
        {
            Tag = _appSettings.MainProxyGroup,
            Outbounds = mainGroupOptions,
            Default = ctx.GeneratedRegions.Values.FirstOrDefault() ?? ctx.AllNodeNames.FirstOrDefault() ?? _appSettings.Direct,
            InterruptExistConnections = true
        });

        var serviceGroupOptions = new List<string> { _appSettings.MainProxyGroup };
        serviceGroupOptions.AddRange(ctx.GeneratedRegions.Values);
        serviceGroupOptions.AddRange(ctx.AllNodeNames);
        serviceGroupOptions.Add(_appSettings.Direct);

        foreach (var service in ProfileDefinitions.Services)
        {
            string defaultSelection = _appSettings.MainProxyGroup;
            if (service.DefaultRegion.HasValue && ctx.GeneratedRegions.TryGetValue(service.DefaultRegion.Value, out string? generatedRegionName))
                defaultSelection = generatedRegionName;

            ctx.ServiceOutbounds.Add(new SelectorOutbound
            {
                Tag = service.Name,
                Outbounds = serviceGroupOptions,
                Default = defaultSelection,
                InterruptExistConnections = true
            });
        }
    }
}