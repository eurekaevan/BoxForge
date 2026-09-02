using System.Text;
using BoxForge.Engine;
using BoxForge.Exceptions;
using Microsoft.Net.Http.Headers;

namespace BoxForge.Server.Api;

internal static class ExportApi
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static void MapExportApi(this WebApplication app)
    {
        app.MapPost("/api/v1/export", ExportAsync);
    }

    private static async Task<IResult> ExportAsync(
        HttpContext context,
        IBoxForgeEngine engine,
        CancellationToken cancellationToken)
    {
        if (!IsMultipartFormData(context.Request.ContentType))
        {
            return ApiProblems.BadRequest(
                "Content-Type 必须是 multipart/form-data。");
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ApiProblems.RequestTooLarge();
        }
        catch (InvalidDataException)
        {
            return ApiProblems.BadRequest("multipart 请求体无效。");
        }

        if (form.Keys.Any(key => !IsAllowedFormField(key)))
        {
            return ApiProblems.BadRequest("请求包含不支持的字段。");
        }

        if (form.Files.Count != 1
            || !string.Equals(
                form.Files[0].Name,
                "file",
                StringComparison.Ordinal))
        {
            return ApiProblems.BadRequest("必须且只能上传一个 file。");
        }

        IFormFile file = form.Files[0];
        if (!TryGetYamlFileStem(file.FileName, out string fileStem))
        {
            return ApiProblems.BadRequest(
                "file 必须使用 .yaml 或 .yml 扩展名。");
        }

        if (file.Length == 0)
        {
            return ApiProblems.BadRequest("上传文件不能为空。");
        }

        if (file.Length > ApiLimits.MaxYamlBytes)
        {
            return ApiProblems.RequestTooLarge();
        }

        if (!TryResolveConfigurationName(form, fileStem, out string name))
        {
            return ApiProblems.BadRequest(
                "配置名必须为 1～100 个 Unicode 字符，且不能包含路径分隔符或控制字符。");
        }

        string?[] platformValues =
        [
            .. form["platforms"].Select(value => (string?)value)
        ];
        if (!ApiRequestValidation.TryParsePlatforms(
                platformValues,
                out var platforms))
        {
            return ApiProblems.BadRequest(
                "平台必须非空、有效且不重复。");
        }

        byte[]? yamlBytes;
        await using (Stream upload = file.OpenReadStream())
        {
            yamlBytes = await BoundedStreamReader.ReadAsync(
                upload,
                ApiLimits.MaxYamlBytes,
                cancellationToken);
        }

        if (yamlBytes == null)
        {
            return ApiProblems.RequestTooLarge();
        }

        string yaml;
        try
        {
            int preambleLength = yamlBytes.AsSpan().StartsWith(
                StrictUtf8.Preamble)
                ? StrictUtf8.Preamble.Length
                : 0;
            yaml = StrictUtf8.GetString(
                yamlBytes,
                preambleLength,
                yamlBytes.Length - preambleLength);
        }
        catch (DecoderFallbackException)
        {
            return ApiProblems.ConversionFailed();
        }

        try
        {
            ConversionBundle bundle = await engine.ConvertAsync(
                new ConversionRequest(name, yaml, platforms),
                cancellationToken);
            byte[] archive = await ConversionZipBuilder.BuildAsync(
                name,
                platforms,
                bundle,
                cancellationToken);

            context.Response.Headers.ContentDisposition =
                "attachment; filename=\"boxforge-output.zip\"";
            return Results.File(archive, "application/zip");
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

    private static bool IsMultipartFormData(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && parsed.MediaType.Equals(
            "multipart/form-data",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedFormField(string key) =>
        string.Equals(key, "name", StringComparison.Ordinal)
        || string.Equals(key, "platforms", StringComparison.Ordinal);

    private static bool TryGetYamlFileStem(
        string fileName,
        out string fileStem)
    {
        const string longExtension = ".yaml";
        const string shortExtension = ".yml";

        int extensionLength;
        if (fileName.EndsWith(
                longExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            extensionLength = longExtension.Length;
        }
        else if (fileName.EndsWith(
                     shortExtension,
                     StringComparison.OrdinalIgnoreCase))
        {
            extensionLength = shortExtension.Length;
        }
        else
        {
            fileStem = string.Empty;
            return false;
        }

        fileStem = fileName[..^extensionLength];
        return fileStem.Length > 0;
    }

    private static bool TryResolveConfigurationName(
        IFormCollection form,
        string fileStem,
        out string name)
    {
        if (form.TryGetValue("name", out var values))
        {
            if (values.Count != 1 || values[0] == null)
            {
                name = string.Empty;
                return false;
            }

            name = values[0]!;
        }
        else
        {
            name = fileStem;
        }

        return ApiRequestValidation.IsValidConfigurationName(name);
    }
}
