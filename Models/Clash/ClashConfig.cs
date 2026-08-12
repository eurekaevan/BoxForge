namespace BoxForge.Models.Clash;

public record ClashConfig
{
    public List<ClashProxyNode> Proxies { get; init; } = [];
    public List<ClashDnsPolicy> DnsPolicies { get; init; } = [];

    public IReadOnlyList<string> FindNodeDnsServers(string server)
    {
        foreach (ClashDnsPolicy policy in DnsPolicies)
        {
            string pattern = policy.Pattern.Trim();
            bool matches;
            if (pattern.StartsWith("+.", StringComparison.Ordinal)
                || pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                string suffix = pattern[2..];
                matches = server.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    || server.EndsWith(
                        $".{suffix}",
                        StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                matches = server.Equals(pattern, StringComparison.OrdinalIgnoreCase);
            }

            if (matches)
            {
                return policy.Servers;
            }
        }

        return [];
    }
}

public sealed record ClashDnsPolicy(
    string Pattern,
    IReadOnlyList<string> Servers);
