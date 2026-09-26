using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Configuration;

public enum AddressFamilyPolicy
{
    Ipv4Only,
    DualStack
}

public static class AddressFamilyPolicies
{
    public static AddressFamilyPolicy For(TargetPlatform platform) => platform switch
    {
        TargetPlatform.Linux or TargetPlatform.Windows or TargetPlatform.Android =>
            AddressFamilyPolicy.Ipv4Only,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
    };

    public static DnsStrategy? ToDnsStrategy(this AddressFamilyPolicy policy) => policy switch
    {
        AddressFamilyPolicy.Ipv4Only => DnsStrategy.Ipv4Only,
        AddressFamilyPolicy.DualStack => null,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };
}
