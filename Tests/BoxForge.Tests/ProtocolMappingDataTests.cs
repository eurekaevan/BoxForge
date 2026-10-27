using System.Collections;
using System.Text.Json;
using BoxForge.Converters;
using BoxForge.Exceptions;
using BoxForge.Models.Clash;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProtocolMappingDataTests
{
    [Test]
    public void ConditionalNestedReadersKeepParentAndChildAliases()
    {
        int guardCalls = 0;
        var schema = new SourceFieldSchema().Mapped("name")
            .Nested("options", new SourceFieldSchema().Mapped("count").Alias("count", "count_alias"),
                (_, _) => { guardCalls++; return null; })
            .Alias("options", "options_alias");
        var node = new ClashProxyNode(new Hashtable
        {
            ["name"] = "safe",
            ["options_alias"] = new Hashtable { ["count_alias"] = "invalid-value" }
        });
        NodeFieldReader reader = schema.Bind(node, out _);
        var error = Assert.Throws<NodeParseException>(() => reader.OptionalObject("options")!.OptionalInt("count"))!;
        Assert.Multiple(() =>
        {
            Assert.That(guardCalls, Is.EqualTo(1));
            Assert.That(error.Diagnostics.Single().Path, Is.EqualTo("source.options_alias.count_alias"));
            Assert.That(error.Message, Does.Contain("字段 'count_alias'").And.Not.Contain("invalid-value"));
        });
    }

    [Test]
    public void AliasLookupDoesNotAllocateForPresentOrMissingFields()
    {
        SourceFieldSchema schema = VlessProtocol.Schema;
        var node = new ClashProxyNode(new Hashtable { ["packet_encoding"] = "xudp" });
        for (int index = 0; index < 100; index++)
        {
            _ = schema.FindAlias(node, "flow");
            _ = schema.FindAlias(node, "packet-encoding");
            _ = schema.FindAlias(node, "client-fingerprint");
        }
        long start = GC.GetAllocatedBytesForCurrentThread();
        int matches = 0;
        for (int index = 0; index < 10_000; index++)
        {
            if (schema.FindAlias(node, "flow") is not null) matches++;
            if (schema.FindAlias(node, "packet-encoding") is not null) matches++;
            if (schema.FindAlias(node, "client-fingerprint") is not null) matches++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.That(matches, Is.EqualTo(10_000));
        Assert.That(allocated, Is.Zero);
    }

    private static string DataPath => Path.Combine(TestContext.CurrentContext.TestDirectory, "protocol-mappings.json");

    [Test]
    public void EvidenceTablePreservesCompleteUniqueMappingsAndPinnedResearchRevisions()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(DataPath));
        JsonElement root = document.RootElement;
        Assert.That(root.EnumerateObject().Select(property => property.Name), Is.EquivalentTo(new[] { "version", "mappings" }));
        Assert.That(root.GetProperty("version").GetInt32(), Is.EqualTo(1));
        JsonElement[] rows = [.. root.GetProperty("mappings").EnumerateArray()];
        Assert.That(rows, Has.Length.AtLeast(30), "Preserve the migrated evidence while allowing new protocol mappings.");
        Assert.That(rows.Select(row => row.GetProperty("id").GetString()), Is.Unique);
        foreach (JsonElement row in rows)
        {
            string id = row.GetProperty("id").GetString()!;
            Assert.That(row.EnumerateObject().Select(property => property.Name), Is.EquivalentTo(new[]
            {
                "id", "source_field", "source_meaning", "target_meaning", "source_evidence", "target_evidence", "status"
            }), id);
            foreach (JsonProperty property in row.EnumerateObject())
                Assert.That(property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()), Is.True, $"{id}.{property.Name}");
            Assert.Multiple(() =>
            {
                Assert.That(row.GetProperty("source_evidence").GetString(), Does.Match(
                    @"^https://github\.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/[^\s]+$"), id);
                Assert.That(row.GetProperty("target_evidence").GetString(), Does.Match(
                    @"^https://github\.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/[^\s]+$"), id);
            });
        }
    }

    private static IEnumerable<TestCaseData> Mappings()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(DataPath));
        foreach (JsonElement row in document.RootElement.GetProperty("mappings").EnumerateArray())
        {
            string id = row.GetProperty("id").GetString()!;
            yield return new TestCaseData(id, row.GetProperty("source_field").GetString()!,
                row.GetProperty("status").GetString()!).SetName("ProtocolMapping_" + id);
        }
    }

    [TestCaseSource(nameof(Mappings))]
    public void DocumentedMappingStillMatchesTheRuntimeSourceSchema(string id, string sourceField, string status)
    {
        SourceFieldSchema schema = id.Split('.')[0] switch
        {
            "vless" => VlessProtocol.Schema,
            "trojan" or "tls" => TrojanProtocol.Schema,
            "anytls" => AnyTlsProtocol.Schema,
            "hysteria2" => Hysteria2Protocol.Schema,
            "shadowsocks" => ShadowsocksProtocol.Schema,
            _ => throw new AssertionException($"Unknown protocol evidence ID: {id}")
        };
        if (id is "shadowsocks.obfs-mode" or "shadowsocks.obfs-host")
        {
            // These fields belong to the guarded plugin-opts nested schema.
            var options = new Hashtable { ["mode"] = "http", ["host"] = "mapped.example.com" };
            options[sourceField] = id == "shadowsocks.obfs-mode" ? "http" : "mapped.example.com";
            var node = new ClashProxyNode(new Hashtable { ["plugin"] = "obfs", ["plugin-opts"] = options });
            Assert.That(status, Is.EqualTo(nameof(SourceFieldDisposition.Mapped)));
            Assert.DoesNotThrow(() => schema.Validate(new NodeFieldReader(node)));
            options["unknown-evidence-field"] = "unsupported";
            Assert.Throws<NodeParseException>(() => schema.Validate(new NodeFieldReader(new ClashProxyNode(
                new Hashtable { ["plugin"] = "obfs", ["plugin-opts"] = options }))));
            return;
        }
        SourceFieldDefinition? field = FindField(schema, sourceField);
        Assert.That(field, Is.Not.Null, $"Documented field {id}/{sourceField} must exist in the runtime schema.");
        Assert.That(field!.Disposition.ToString(), Is.EqualTo(status), id);
    }

    private static SourceFieldDefinition? FindField(SourceFieldSchema schema, string name)
    {
        foreach (SourceFieldDefinition field in schema.Fields)
        {
            if (field.Name == name) return field;
            if (field.NestedSchema is { } nested && FindField(nested, name) is { } match) return match;
        }
        return null;
    }
}
