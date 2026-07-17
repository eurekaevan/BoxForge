using SubConvert.Models;
using SubConvert.Models.Singbox;
using SubConvert.Helpers;
using SubConvert.Extensions;
using SubConvert.Exceptions;

namespace SubConvert.Converters;

public class Hysteria2Converter : IProxyConverter
{
    public bool CanHandle(string proxyType) => proxyType == "hysteria2";

    public NodeConversionResult Convert(Dictionary<string, object> p)
    {
        string name = p.GetString("name") ?? "Unknown-HY2-Node";

        try
        {
            string server = p.GetRequiredString("server");

            // 解析跳跃端口逻辑
            int? serverPort = null;
            List<string>? serverPorts = null;

            string? portsStr = p.GetString("ports");
            if (!string.IsNullOrWhiteSpace(portsStr))
            {
                if (portsStr.Contains(','))
                {
                    serverPorts = [.. portsStr
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => p.Replace('-', ':'))];
                }
                else if (portsStr.Contains('-'))
                {
                    serverPorts = [portsStr.Replace('-', ':')];
                }
                else if (int.TryParse(portsStr, out int singlePort))
                {
                    serverPort = singlePort;
                }
            }
            else
            {
                serverPort = p.GetInt("port");
            }

            if (serverPort == null && serverPorts == null)
                throw new NodeParseException("未找到有效端口 (需配置 port 或 ports)");

            // 组装混淆配置 (可选)
            OutboundObfs? obfsConfig = null;
            string? obfsType = p.GetString("obfs");
            if (obfsType != null)
            {
                obfsConfig = new OutboundObfs { Type = obfsType, Password = p.GetString("obfs-password") };
            }

            return NodeConversionResult.Success(new Hysteria2Outbound
            {
                Tag = name,
                Server = server,
                ServerPort = serverPort,
                ServerPorts = serverPorts,
                Obfs = obfsConfig,
                Password = p.GetRequiredString("password"),
                Tls = TlsConfigHelper.Extract(p, server, forceTls: true),
                DomainResolver = "node-resolver",
                ConnectTimeout = "5s"                                                
            });
        }
        catch (NodeParseException ex)
        {
            return NodeConversionResult.Fail($"Hysteria2 节点 '{name}' 解析失败 -> {ex.Message}");
        }
    }
}