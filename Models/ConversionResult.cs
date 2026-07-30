using BoxForge.Models.Singbox;

namespace BoxForge.Models;

public record ConversionResult(
    SingboxConfig Config,
    string JsonContent
);