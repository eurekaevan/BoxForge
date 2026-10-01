using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;

namespace BoxForge.Converters;

public sealed class TrojanConverter()
    : ProxyConverterBase("Trojan", "trojan")
{
    private static readonly SourceFieldSchema SourceFields = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: true, supportsUtls: true, forceTls: true))
        .Include(SourceFieldSchemas.TcpTransport())
        .Mapped("password")
        .Unsupported("Trojan Shadowsocks 加密选项尚未映射", "ss-opts", "ss_opts");

    protected override SourceFieldSchema Schema => SourceFields;

    protected override ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name)
    {
        string server = node.GetRequiredString("server");

        return new TrojanOutbound
        {
            Tag = name,
            Server = server,
            ServerPort = node.GetRequiredInt("port"),
            Password = node.GetRequiredString("password"),
            Tls = TlsConfigHelper.Extract(node, server, forceTls: true)
        };
    }
}
