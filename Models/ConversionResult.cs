using SubConvert.Models.Singbox;

namespace SubConvert.Models;

public record ConversionResult(
    SingboxConfig Config,
    string JsonContent
);