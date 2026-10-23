using BoxForge.Models.Singbox;

namespace BoxForge.Configuration;

public enum AddressFamilyPolicy
{
    Ipv4Only,
    DualStack,
    Auto
}

public static class AddressFamilyPolicies
{
    internal const string GlobalIpv6Prefix = "2000::/3";

    public static DnsStrategy? ToDnsStrategy(this AddressFamilyPolicy policy) => policy switch
    {
        AddressFamilyPolicy.Ipv4Only => DnsStrategy.Ipv4Only,
        AddressFamilyPolicy.DualStack or AddressFamilyPolicy.Auto => null,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };
}
