using System.Diagnostics;

namespace BoxForge.Server.Api;

internal static partial class ApiProblems
{
    public static IResult BadRequest(string detail) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "请求无效",
        detail: detail);

    public static IResult RequestTooLarge() => Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "请求过大",
        detail: "请求体不能超过 4 MiB，YAML 内容不能超过 2 MiB。");

    public static IResult ConversionFailed() => Results.Problem(
        statusCode: StatusCodes.Status422UnprocessableEntity,
        title: "转换失败",
        detail: "Clash YAML 无法转换为 sing-box 配置。");

    public static IResult UnexpectedError(HttpContext context, Exception exception)
    {
        // Exception messages and objects can contain subscription credentials.
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("BoxForge.Server.Api");
        string traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        LogUnexpectedError(logger, traceId, context.Request.Path.Value,
            exception.GetType().FullName, exception.StackTrace);
        return Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "服务器内部错误",
            detail: "处理请求时发生未预期错误。",
            extensions: new Dictionary<string, object?> { ["traceId"] = traceId });
    }

    [LoggerMessage(1, LogLevel.Error, "请求 {TraceId} 在 {Endpoint} 失败，异常类型 {ExceptionType}。调用栈：{StackTrace}")]
    private static partial void LogUnexpectedError(ILogger logger, string traceId,
        string? endpoint, string? exceptionType, string? stackTrace);
}
