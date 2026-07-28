namespace SubConvert.Models.Clash;

public record ClashConfig
{
    public List<ClashProxyNode> Proxies { get; init; } = [];
}
