namespace BoxForge.Configuration;

public static class DnsResponseTags
{
    public const string DomesticPrimary = "response-domestic-primary";
    public const string DomesticSecondary = "response-domestic-secondary";
    public const string GlobalPrimary = "response-global-primary";
    public const string GlobalSecondary = "response-global-secondary";

    public static IReadOnlyList<string> BaseTags { get; } = Array.AsReadOnly<string>(
    [
        DomesticPrimary,
        DomesticSecondary,
        GlobalPrimary,
        GlobalSecondary
    ]);

    public static string ForAddressQuery(string responseTag) => responseTag + "-a";

    internal static string ForAddressQuery(string responseTag, string queryType) =>
        responseTag + "-" + queryType.ToLowerInvariant();
}
