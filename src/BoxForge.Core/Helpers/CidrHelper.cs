using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace BoxForge.Helpers;

internal readonly record struct Cidr(AddressFamily Family, byte[] Network, int PrefixLength)
{
    internal bool Contains(IPAddress address)
    {
        if (address.AddressFamily != Family || address.IsIPv4MappedToIPv6) return false;
        byte[] bytes = address.GetAddressBytes();
        return CidrHelper.NetworkEquals(Network, bytes, PrefixLength);
    }
}

internal static class CidrHelper
{
    internal static bool TryParseCanonical(string? value, out Cidr cidr)
    {
        cidr = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] parts = value.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || address.IsIPv4MappedToIPv6 || address.ToString() != parts[0]
            || parts[1].Length == 0 || (parts[1].Length > 1 && parts[1][0] == '0')
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix)
            || prefix < 0 || prefix > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)
            || parts[1] != prefix.ToString(CultureInfo.InvariantCulture))
            return false;
        byte[] network = address.GetAddressBytes();
        if (!HostBitsAreZero(network, prefix)) return false;
        cidr = new Cidr(address.AddressFamily, network, prefix);
        return true;
    }

    private static bool HostBitsAreZero(byte[] network, int prefixLength)
    {
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        if (remainingBits != 0)
        {
            int hostMask = 0xFF >> remainingBits;
            if ((network[fullBytes] & hostMask) != 0) return false;
            fullBytes++;
        }
        for (int index = fullBytes; index < network.Length; index++)
        {
            if (network[index] != 0) return false;
        }
        return true;
    }

    internal static bool Overlaps(Cidr left, Cidr right) =>
        left.Family == right.Family
        && (NetworkEquals(left.Network, right.Network, left.PrefixLength)
            || NetworkEquals(right.Network, left.Network, right.PrefixLength));

    internal static bool NetworkEquals(byte[] left, byte[] right, int prefixLength)
    {
        if (left.Length != right.Length || prefixLength < 0 || prefixLength > left.Length * 8) return false;
        int fullBytes = prefixLength / 8;
        if (!left.AsSpan(0, fullBytes).SequenceEqual(right.AsSpan(0, fullBytes))) return false;
        int remainingBits = prefixLength % 8;
        if (remainingBits == 0) return true;
        int mask = 0xFF << (8 - remainingBits);
        return (left[fullBytes] & mask) == (right[fullBytes] & mask);
    }
}
