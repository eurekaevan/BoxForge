using BoxForge.Models;

namespace BoxForge.Exceptions;

public sealed class BoxForgePlatformConversionException(
    TargetPlatform platform,
    Exception innerException)
    : BoxForgeConversionException(
        $"平台 {platform} 转换失败。",
        innerException)
{
    public TargetPlatform Platform { get; } = platform;
}
