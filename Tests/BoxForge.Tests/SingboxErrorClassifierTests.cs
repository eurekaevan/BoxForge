using BoxForge.Services;

namespace BoxForge.Tests;

[TestFixture]
public sealed class SingboxErrorClassifierTests
{
    [TestCase("dial tcp: lookup hidden.example: no such host", "dns-failure")]
    [TestCase("remote error: tls: bad certificate", "tls-certificate")]
    [TestCase(
        "tls handshake failed password=api-key-must-not-leak",
        "tls-handshake")]
    [TestCase("REALITY handshake failed", "reality-handshake")]
    [TestCase("QUIC connection timeout", "quic-failure")]
    [TestCase("connect: connection refused", "connection-refused")]
    [TestCase("read: connection reset by peer", "connection-reset")]
    [TestCase("dial tcp: i/o timeout", "upstream-timeout")]
    public void MapsRawSingboxErrorsToCredentialSafeReasons(
        string rawError,
        string expected)
    {
        var result = SingboxErrorClassifier.Classify(rawError);

        Assert.That(result?.Reason, Is.EqualTo(expected));
        Assert.That(result?.Reason, Does.Not.Contain("hidden.example"));
        Assert.That(result?.Reason, Does.Not.Contain("must-not-leak"));
    }

    [Test]
    public void IgnoresUnrecognizedLinesInsteadOfLoggingRawContent()
    {
        var result = SingboxErrorClassifier.Classify(
            "unknown failure containing api-key-must-not-leak");

        Assert.That(result, Is.Null);
    }
}
