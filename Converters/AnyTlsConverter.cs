using SubConvert.Models;
using SubConvert.Models.Singbox;
using SubConvert.Helpers;
using SubConvert.Extensions;
using SubConvert.Exceptions;

namespace SubConvert.Converters;

public class AnyTlsConverter : IProxyConverter
{
    public bool CanHandle(string proxyType) => 
        string.Equals(proxyType, "anytls", StringComparison.OrdinalIgnoreCase);

    public NodeConversionResult Convert(Dictionary<string, object> p)
    {
        string name = p.GetString("name") ?? "Unknown-AnyTLS-Node";

        try
        {
            string server = p.GetRequiredString("server");

            return NodeConversionResult.Success(new AnyTlsOutbound
            {
                Tag = name,
                Server = server,
                ServerPort = p.GetRequiredInt("port"),
                Password = p.GetRequiredString("password"),
                IdleTimeout = p.GetString("idle-timeout") ?? p.GetString("idle_timeout"),
                Tls = TlsConfigHelper.Extract(p, server, forceTls: true),
                DomainResolver = "node-resolver",
                ConnectTimeout = "5s"
            });
        }
        catch (NodeParseException ex)
        {
            return NodeConversionResult.Fail($"AnyTLS 节点 '{name}' 解析失败 -> {ex.Message}");
        }
    }
}
