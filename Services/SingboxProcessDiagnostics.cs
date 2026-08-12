using System.Diagnostics;

namespace BoxForge.Services;

internal sealed class SingboxProcessDiagnostics
{
    private readonly Lock syncRoot = new();
    private string? failureReason;
    private int failurePriority;

    public string? FailureReason
    {
        get
        {
            lock (syncRoot)
            {
                return failureReason;
            }
        }
    }

    public void Observe(object sender, DataReceivedEventArgs args)
    {
        if (SingboxErrorClassifier.Classify(args.Data) is not { } classified)
        {
            return;
        }

        lock (syncRoot)
        {
            if (classified.Priority > failurePriority)
            {
                failurePriority = classified.Priority;
                failureReason = classified.Reason;
            }
        }
    }
}

internal static class SingboxErrorClassifier
{
    internal sealed record Classification(string Reason, int Priority);

    public static Classification? Classify(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        string value = line.ToLowerInvariant();
        if (ContainsAny(
            value,
            "authentication failed",
            "authentication failure",
            "invalid password",
            "bad password"))
        {
            return new("authentication-failed", 100);
        }

        if (value.Contains("reality", StringComparison.Ordinal))
        {
            return new("reality-handshake", 95);
        }

        if (ContainsAny(value, "x509", "certificate"))
        {
            return new("tls-certificate", 90);
        }

        if (value.Contains("quic", StringComparison.Ordinal))
        {
            return new("quic-failure", 85);
        }

        if (ContainsAny(value, "handshake", "tls:"))
        {
            return new("tls-handshake", 80);
        }

        if (ContainsAny(
            value,
            "no such host",
            "name resolution",
            "server misbehaving",
            "nxdomain"))
        {
            return new("dns-failure", 75);
        }

        if (value.Contains("connection refused", StringComparison.Ordinal))
        {
            return new("connection-refused", 70);
        }

        if (ContainsAny(
            value,
            "network is unreachable",
            "no route to host"))
        {
            return new("network-unreachable", 65);
        }

        if (ContainsAny(
            value,
            "connection reset",
            "reset by peer"))
        {
            return new("connection-reset", 60);
        }

        if (ContainsAny(
            value,
            "unexpected eof",
            "closed pipe",
            "connection closed"))
        {
            return new("connection-closed", 55);
        }

        if (ContainsAny(
            value,
            "i/o timeout",
            "deadline exceeded",
            "timed out"))
        {
            return new("upstream-timeout", 50);
        }

        return null;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));
}
