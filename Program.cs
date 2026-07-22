using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SubConvert.App;
using SubConvert.Builders;
using SubConvert.Builders.Components;
using SubConvert.Configuration;
using SubConvert.Converters;
using SubConvert.Parsers;
using SubConvert.Services;
using SubConvert.Ui;
using SubConvert.Workflows;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddEnvironmentVariables(prefix: "SUBCONVERT_");
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<AppSettings>(context.Configuration);
        services.AddTransient<IUserInterface, ConsoleUi>();

        // 注册核心基础服务
        services.AddSingleton<IClashParser, ClashParser>();
        services.AddSingleton<IConfigSerializer, ConfigSerializer>();

        // 注册协议转换器
        services.AddTransient<IProxyConverter, TrojanConverter>();
        services.AddTransient<IProxyConverter, VlessConverter>();
        services.AddTransient<IProxyConverter, Hysteria2Converter>();
        services.AddTransient<IProxyConverter, ShadowsocksConverter>();
        services.AddTransient<IProxyConverter, AnyTlsConverter>();

        // 注册 Builder 流水线组件 (按照执行顺序注册)
        // 使用 AddTransient，系统解析 IEnumerable<IConfigComponentBuilder> 时会自动聚合成集合
        services.AddTransient<IConfigComponentBuilder, InboundBuilder>();
        services.AddTransient<IConfigComponentBuilder, OutboundGroupBuilder>();
        services.AddTransient<IConfigComponentBuilder, DnsProfileBuilder>();
        services.AddTransient<IConfigComponentBuilder, RouteProfileBuilder>();

        // 注册总装配车间
        services.AddTransient<ISingboxConfigBuilder, SingboxConfigBuilder>();
        services.AddTransient<ConversionService>();
        services.AddTransient<GitHubWorkflow>();
        
        services.AddTransient<ConversionOrchestrator>();
    })
    .Build();

var app = host.Services.GetRequiredService<ConversionOrchestrator>();
await app.RunAsync();