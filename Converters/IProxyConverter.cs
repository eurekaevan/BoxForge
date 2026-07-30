using SubConvert.Models;
using SubConvert.Models.Clash;

namespace SubConvert.Converters;

public interface IProxyConverter
{
    bool CanHandle(string proxyType);

    NodeConversionResult Convert(ClashProxyNode proxy);
}
