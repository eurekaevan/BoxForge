using System.Net.Sockets;
using BoxForge.Helpers;

namespace BoxForge.Configuration;

internal static class FakeIpRangePolicy
{
    // Matches the pinned core's fakeip store, which reserves 1024 addresses per family.
    internal const int MinimumUsableAddresses = 1024;

    private const string Inet4Allowed = "198.18.0.0/15";
    private const string Inet6Allowed = "2001:2::/48";

    internal static bool HasMinimumCapacity(Cidr range) =>
        CidrHelper.HasUsableAddressCount(range, MinimumUsableAddresses);

    // Keep synthetic addresses within the reserved benchmark ranges.
    internal static bool IsAllowed(Cidr range) =>
        range.Family switch
        {
            AddressFamily.InterNetwork => IsWithin(Inet4Allowed, range),
            AddressFamily.InterNetworkV6 => IsWithin(Inet6Allowed, range),
            _ => false
        };

    private static bool IsWithin(string allowed, Cidr range) =>
        CidrHelper.TryParseCanonical(allowed, out Cidr allowedRange)
        && CidrHelper.Contains(allowedRange, range);
}
