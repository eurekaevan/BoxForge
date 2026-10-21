using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;
using System.Globalization;

namespace BoxForge.Builders;

internal static class SingboxNodeEmitter
{
    internal static ProxyOutbound Emit(ProxyNode node) => node switch
    {
        VlessNode vless => Emit(vless),
        TrojanNode trojan => Emit(trojan),
        AnyTlsNode anyTls => Emit(anyTls),
        Hysteria2Node hysteria2 => Emit(hysteria2),
        ShadowsocksNode shadowsocks => Emit(shadowsocks),
        _ => throw new InvalidOperationException("Unknown intermediate node type.")
    };

    internal static VlessOutbound Emit(VlessNode node)
    {
        node.Validate();
        return new VlessOutbound
        {
            Tag = node.Name,
            Server = node.Server,
            ConnectTimeout = node.Connection.ConnectTimeout,
            TcpKeepAlive = node.Connection.TcpKeepAlive,
            TcpKeepAliveInterval = node.Connection.TcpKeepAliveInterval,
            ServerPort = node.Port,
            Uuid = node.UserId,
            Flow = node.Flow switch
            {
                VlessFlow.None => null,
                VlessFlow.Vision => "xtls-rprx-vision",
                _ => throw new ArgumentOutOfRangeException(nameof(node), "Unknown VLESS flow.")
            },
            PacketEncoding = node.DatagramEncoding switch
            {
                DatagramEncoding.Xudp => "xudp",
                DatagramEncoding.PacketAddress => "packetaddr",
                _ => throw new ArgumentOutOfRangeException(nameof(node), "Unknown datagram encoding.")
            },
            Tls = EmitTls(node.Tls)
        };
    }

    internal static TrojanOutbound Emit(TrojanNode node)
    {
        node.Validate();
        return new TrojanOutbound
        {
            Tag = node.Name,
            Server = node.Server,
            ConnectTimeout = node.Connection.ConnectTimeout,
            TcpKeepAlive = node.Connection.TcpKeepAlive,
            TcpKeepAliveInterval = node.Connection.TcpKeepAliveInterval,
            ServerPort = node.Port,
            Password = node.Password,
            Tls = EmitTls(node.Tls)
        };
    }

    internal static AnyTlsOutbound Emit(AnyTlsNode node)
    {
        node.Validate();
        return new AnyTlsOutbound
        {
            Tag = node.Name,
            Server = node.Server,
            ConnectTimeout = node.Connection.ConnectTimeout,
            TcpKeepAlive = node.Connection.TcpKeepAlive,
            TcpKeepAliveInterval = node.Connection.TcpKeepAliveInterval,
            ServerPort = node.Port,
            Password = node.Password,
            IdleSessionTimeout = node.IdleTimeout,
            IdleSessionCheckInterval = node.IdleCheckInterval,
            MinIdleSession = node.MinimumIdleSessions,
            ClientMetadata = node.ClientMetadata,
            Tls = EmitTls(node.Tls)
        };
    }

    internal static Hysteria2Outbound Emit(Hysteria2Node node)
    {
        node.Validate();
        var ports = node.Ports;
        bool singlePort = ports.Count == 1 && ports[0].End is null;
        var obfs = node.Obfuscation;
        return new Hysteria2Outbound
        {
            Tag = node.Name,
            Server = node.Server,
            ConnectTimeout = node.Connection.ConnectTimeout,
            TcpKeepAlive = node.Connection.TcpKeepAlive,
            TcpKeepAliveInterval = node.Connection.TcpKeepAliveInterval,
            ServerPort = singlePort ? ports[0].Start : null,
            ServerPorts = singlePort ? null : ports.Select(port =>
                port.Start.ToString(CultureInfo.InvariantCulture) + ":"
                + (port.End ?? port.Start).ToString(CultureInfo.InvariantCulture)).ToList(),
            Password = node.Password,
            UpMbps = node.UploadMegabitsPerSecond,
            DownMbps = node.DownloadMegabitsPerSecond,
            HopInterval = (node.HopInterval ?? node.TuningHopInterval) is { } hop
                ? hop.MinimumSeconds.ToString(CultureInfo.InvariantCulture) + "s" : null,
            HopIntervalMax = (node.HopInterval ?? node.TuningHopInterval)?.MaximumSeconds is { } maximum
                ? maximum.ToString(CultureInfo.InvariantCulture) + "s" : null,
            BbrProfile = (node.BbrProfile ?? node.TuningBbrProfile) switch
            {
                null => null,
                Hysteria2BbrProfile.Standard => "standard",
                Hysteria2BbrProfile.Conservative => "conservative",
                Hysteria2BbrProfile.Aggressive => "aggressive",
                _ => throw new InvalidOperationException("Unknown Hysteria2 BBR profile.")
            },
            Obfs = obfs is null ? null : new OutboundObfs
            {
                Type = obfs.Kind switch
                {
                    Hysteria2ObfuscationKind.Salamander => "salamander",
                    Hysteria2ObfuscationKind.Gecko => "gecko",
                    _ => throw new InvalidOperationException("Unknown Hysteria2 obfuscation kind.")
                },
                Password = obfs.Password,
                MinPacketSize = obfs.MinimumPacketSize.Value,
                MaxPacketSize = obfs.MaximumPacketSize.Value
            },
            Tls = EmitTls(node.Tls)
        };
    }

    internal static ShadowsocksOutbound Emit(ShadowsocksNode node)
    {
        node.Validate();
        var plugin = node.Plugin;
        string? mode = plugin?.Mode switch
        {
            null => null,
            ShadowsocksObfsMode.Http => "http",
            ShadowsocksObfsMode.Tls => "tls",
            _ => throw new InvalidOperationException("Unknown Shadowsocks obfs mode.")
        };
        return new ShadowsocksOutbound
        {
            Tag = node.Name,
            Server = node.Server,
            ConnectTimeout = node.Connection.ConnectTimeout,
            TcpKeepAlive = node.Connection.TcpKeepAlive,
            TcpKeepAliveInterval = node.Connection.TcpKeepAliveInterval,
            ServerPort = node.Port,
            Method = node.Cipher,
            Password = node.Password,
            Plugin = plugin is null ? null : "obfs-local",
            PluginOpts = plugin is null ? null : $"obfs={EscapePluginOption(mode!)};obfs-host={EscapePluginOption(plugin.Host)}",
            UdpOverTcp = node.UdpOverTcpEnabled == true ? new UdpOverTcpOptions
            {
                Enabled = true,
                Version = (int)node.UdpOverTcpVersion
            } : null
        };
    }

    private static string EscapePluginOption(string value) => value.Replace("\\", "\\\\")
        .Replace(";", "\\;").Replace("=", "\\=");

    internal static OutboundTls? EmitTls(TlsSettings? tls)
    {
        if (tls is null) return null;
        tls.Validate();
        return new OutboundTls
        {
            Enabled = true,
            ServerName = tls.ServerName,
            Insecure = tls.AllowInvalidCertificate,
            Alpn = tls.ApplicationProtocols?.ToList(),
            Utls = tls.ClientFingerprint is { } fingerprint
                ? new Utls { Enabled = true, Fingerprint = fingerprint } : null,
            Reality = tls.Reality is { } reality
                ? new OutboundReality
                {
                    Enabled = true,
                    PublicKey = reality.PublicKey,
                    ShortId = reality.ShortId
                }
                : null
        };
    }
}
