namespace BoxForge.Configuration;

public sealed class SingboxOptions
{
    public const string RuleSetHttpClientTag = "rule-set-download";

    public string MainProxyGroup { get; set; } = "🚀 PROXIES";
    public string Direct { get; set; } = "DIRECT";
    public string ApiSecret { get; set; } = "";
}
