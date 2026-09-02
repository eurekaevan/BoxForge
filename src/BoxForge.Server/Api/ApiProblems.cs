namespace BoxForge.Server.Api;

internal static class ApiProblems
{
    public static IResult BadRequest(string detail) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "请求无效",
        detail: detail);

    public static IResult RequestTooLarge() => Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "请求过大",
        detail: "请求体不能超过 4 MiB，yaml 不能超过 2 MiB。");

    public static IResult ConversionFailed() => Results.Problem(
        statusCode: StatusCodes.Status422UnprocessableEntity,
        title: "转换失败",
        detail: "Clash YAML 无法转换为 sing-box 配置。");

    public static IResult UnexpectedError() => Results.Problem(
        statusCode: StatusCodes.Status500InternalServerError,
        title: "服务器内部错误",
        detail: "处理请求时发生未预期错误。");
}
