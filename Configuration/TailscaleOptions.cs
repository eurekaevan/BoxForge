namespace BoxForge.Configuration;

public sealed class TailscaleOptions
{
    public bool Enabled { get; set; }
    public string Tag { get; set; } = "tailscale";
    public string DnsTag { get; set; } = "tailscale-dns";
    public string StateDirectory { get; set; } = "tailscale";
    public string ControlUrl { get; set; } = "";
    public string Hostname { get; set; } = "";
    public bool AcceptRoutes { get; set; } = true;
    public string ExitNode { get; set; } = "";
    public bool ExitNodeAllowLanAccess { get; set; }
    public string? TaildropDirectory { get; set; }
}
