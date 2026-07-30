using BoxForge.Models;
using BoxForge.Models.Clash;

namespace BoxForge.Converters;

public interface IProxyConverter
{
    bool CanHandle(string proxyType);

    NodeConversionResult Convert(ClashProxyNode proxy);
}
