using System.Collections.Immutable;
using BoxForge.Models;

namespace BoxForge.Exceptions;

/// <summary>A profile error whose diagnostics contain schema locations, never input values.</summary>
public sealed class ProfileValidationException : Exception
{
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }

    public ProfileValidationException(string path, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostics = ImmutableArray.Create(new ConfigDiagnostic("PF001", path, "配置字段或覆盖操作无效。"));
    }

    private ProfileValidationException(ProfileValidationException error, string origin)
        : base(error.Message, error)
    {
        Diagnostics = error.Diagnostics.Select(diagnostic => diagnostic with
        {
            Path = origin + (diagnostic.Path.StartsWith("profile.", StringComparison.Ordinal)
                ? diagnostic.Path["profile".Length..] : "")
        }).ToImmutableArray();
    }

    internal ProfileValidationException AtOrigin(string origin) => new(this, origin);
}
