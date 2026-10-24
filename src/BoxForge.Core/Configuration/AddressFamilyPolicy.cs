using BoxForge.Models.Singbox;

namespace BoxForge.Configuration;

public enum AddressFamilyPolicy
{
    Ipv4Only,
    DualStack,
    Auto,
    DirectDualStack
}

public static class AddressFamilyPolicies
{
    internal const string GlobalIpv6Prefix = "2000::/3";

    public static DnsStrategy? ToDnsStrategy(this AddressFamilyPolicy policy) => policy switch
    {
        AddressFamilyPolicy.Ipv4Only => DnsStrategy.Ipv4Only,
        AddressFamilyPolicy.DualStack or AddressFamilyPolicy.Auto or AddressFamilyPolicy.DirectDualStack => null,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };

    internal static bool UsesInterfaceGuard(this AddressFamilyPolicy policy) =>
        policy is AddressFamilyPolicy.Auto or AddressFamilyPolicy.DirectDualStack;

    // Node, rule-set and dashboard resolution may ride the IPv4-only proxy path.
    internal static AddressFamilyPolicy ForControlPlane(this AddressFamilyPolicy policy) =>
        policy == AddressFamilyPolicy.DirectDualStack ? AddressFamilyPolicy.Ipv4Only : policy;
}
