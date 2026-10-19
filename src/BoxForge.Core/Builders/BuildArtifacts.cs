using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Configuration;

namespace BoxForge.Builders;

public sealed record SingboxBuildRequest(
    NodeCatalog Nodes,
    TargetPlatform Platform,
    string? CacheId);

public sealed record NodeCatalog(
    IReadOnlyList<ProxyOutbound> Outbounds,
    IReadOnlyList<string> Names
)
{
    private Lazy<IReadOnlyList<ProxyOutbound>> compatibilityOutbounds = new(() => Outbounds);

    // Keep the public constructor, property, with-expressions and deconstruction.
    // Engine callers use Entries; this target view is materialized only on request.
    public IReadOnlyList<ProxyOutbound> Outbounds
    {
        get => compatibilityOutbounds.Value;
        init
        {
            compatibilityOutbounds = new(() => value);
            Entries = null;
        }
    }

    internal IReadOnlyList<NodeConversionResult>? Entries { get; private init; }

    internal IEnumerable<string> GetLeafTags() => Entries is { } entries && !compatibilityOutbounds.IsValueCreated
        ? entries.Select(entry => entry switch
        {
            ParsedNode parsed => parsed.Node.Name,
            ConvertedNode converted => converted.Outbound.Tag,
            _ => throw new InvalidOperationException("A node catalog may contain only successful nodes.")
        })
        : Outbounds.Select(outbound => outbound.Tag);

    public bool Equals(NodeCatalog? other) => other is not null
        && EqualityComparer<IReadOnlyList<ProxyOutbound>>.Default.Equals(Outbounds, other.Outbounds)
        && EqualityComparer<IReadOnlyList<string>>.Default.Equals(Names, other.Names);

    public override int GetHashCode() => HashCode.Combine(Outbounds, Names);

    internal static NodeCatalog FromEntries(IReadOnlyList<NodeConversionResult> entries, IReadOnlyList<string> names) =>
        new([], names)
        {
            Entries = entries,
            compatibilityOutbounds = new(() => entries.Select(entry => entry switch
            {
                ParsedNode parsed => SingboxNodeEmitter.Emit(parsed.Node),
                ConvertedNode converted => converted.Outbound,
                _ => throw new InvalidOperationException("A node catalog may contain only successful nodes.")
            }).ToArray())
        };

    // Accessing the old mutable target view opts into its original semantics.
    internal IEnumerable<ProxyOutbound> Emit(PlatformProfile platform) => Entries is { } entries && !compatibilityOutbounds.IsValueCreated
        ? entries.Select(entry => entry switch
        {
            ParsedNode parsed => SingboxNodeEmitter.Emit(OutboundTuningPolicy.Apply(parsed.Node, platform)),
            ConvertedNode converted => OutboundTuningPolicy.Apply(converted.Outbound, platform),
            _ => throw new InvalidOperationException("A node catalog may contain only successful nodes.")
        })
        : Outbounds.Select(outbound => OutboundTuningPolicy.Apply(outbound, platform));
}

public sealed record ProfilePlan(
    SelectorOutbound MainOutbound,
    IReadOnlyList<SelectorOutbound> RegionOutbounds,
    IReadOnlyList<UrlTestOutbound> RegionAutoOutbounds,
    IReadOnlyList<SelectorOutbound> ServiceOutbounds,
    DirectOutbound DirectOutbound
);
