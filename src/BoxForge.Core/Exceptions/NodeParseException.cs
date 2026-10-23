using System.Collections.Immutable;
using BoxForge.Models;

namespace BoxForge.Exceptions;

public sealed class NodeParseException : Exception
{
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }

    public NodeParseException(string message)
        : this(message, new ConfigDiagnostic("SRC001", "source", "来源节点无法解析。"))
    {
    }

    internal NodeParseException(string message, ConfigDiagnostic diagnostic, Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostics = ImmutableArray.Create(diagnostic);
    }

    internal NodeParseException AtPath(string prefix)
    {
        ConfigDiagnostic diagnostic = Diagnostics[0];
        string path = diagnostic.Path == "source" ? prefix : prefix + diagnostic.Path["source".Length..];
        return new NodeParseException(Message, diagnostic with { Path = path }, this);
    }
}
