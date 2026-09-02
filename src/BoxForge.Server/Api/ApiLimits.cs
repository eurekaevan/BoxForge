namespace BoxForge.Server.Api;

internal static class ApiLimits
{
    public const int MaxRequestBodyBytes = 4 * 1024 * 1024;
    public const int MaxYamlBytes = 2 * 1024 * 1024;
    public const int MaxConfigurationNameRunes = 100;
}
