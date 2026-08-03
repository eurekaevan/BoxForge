namespace BoxForge.Models.Clash;

public record ClashConfig
{
    public List<ClashProxyNode> Proxies { get; init; } = [];
}
