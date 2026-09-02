using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoxForge.Engine;
using BoxForge.Exceptions;

namespace BoxForge.Server.Api;

internal static class ConversionApi
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void MapConversionApi(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok")));
        app.MapPost("/api/v1/convert", ConvertAsync);
    }

    public static async Task RejectOversizedRequestsAsync(
        HttpContext context,
        RequestDelegate next)
    {
        if (context.Request.ContentLength is > ApiLimits.MaxRequestBodyBytes)
        {
            await ApiProblems.RequestTooLarge().ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static async Task<IResult> ConvertAsync(
        HttpContext context,
        IBoxForgeEngine engine,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            return ApiProblems.BadRequest(
                "Content-Type 必须是 application/json。");
        }

        byte[]? body;
        try
        {
            body = await ReadRequestBodyAsync(
                context.Request.Body,
                cancellationToken);
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ApiProblems.RequestTooLarge();
        }

        if (body == null)
        {
            return ApiProblems.RequestTooLarge();
        }

        ConvertApiRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ConvertApiRequest>(
                body,
                RequestJsonOptions);
        }
        catch (JsonException)
        {
            return ApiProblems.BadRequest(
                "请求体必须是有效的 JSON。");
        }

        if (request == null)
        {
            return ApiProblems.BadRequest("请求体不能为空。");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ApiProblems.BadRequest("name 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(request.Yaml))
        {
            return ApiProblems.BadRequest("yaml 不能为空。");
        }

        if (Encoding.UTF8.GetByteCount(request.Yaml) > ApiLimits.MaxYamlBytes)
        {
            return ApiProblems.RequestTooLarge();
        }

        if (!ApiRequestValidation.TryParsePlatforms(
                request.Platforms,
                out var platforms))
        {
            return ApiProblems.BadRequest(
                "平台必须非空、有效且不重复。");
        }

        try
        {
            ConversionBundle bundle = await engine.ConvertAsync(
                new ConversionRequest(request.Name, request.Yaml, platforms),
                cancellationToken);
            return Results.Ok(new ConvertApiResponse(
                bundle.Name,
                [
                    .. bundle.Artifacts.Select(artifact =>
                        new ConvertApiArtifact(
                            artifact.Platform.ToString(),
                            $"{bundle.Name}/{artifact.Platform}/config.json",
                            artifact.Sha256,
                            artifact.Content))
                ]));
        }
        catch (ArgumentException)
        {
            return ApiProblems.BadRequest("请求参数无效。");
        }
        catch (BoxForgeConversionException)
        {
            return ApiProblems.ConversionFailed();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiProblems.UnexpectedError();
        }
    }

    private static async Task<byte[]?> ReadRequestBodyAsync(
        Stream body,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (content.Length <= ApiLimits.MaxRequestBodyBytes)
            {
                int remaining = checked(
                    ApiLimits.MaxRequestBodyBytes + 1 - (int)content.Length);
                int read = await body.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await content.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }

            return content.Length > ApiLimits.MaxRequestBodyBytes
                ? null
                : content.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
