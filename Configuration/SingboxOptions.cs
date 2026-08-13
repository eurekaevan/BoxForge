namespace BoxForge.Configuration;

public sealed class SingboxOptions
{
    public const string RuleSetHttpClientTag = "rule-set-download";
    public const string AdGuardDnsRuleSetTag = "adguard-dns";

    public string MainProxyGroup { get; set; } = "🚀 PROXIES";
    public string Direct { get; set; } = "DIRECT";
    public string AdGuardDnsRuleSetUrl { get; set; } = "";
}
