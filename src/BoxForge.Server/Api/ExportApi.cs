using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        // MultipartReader reports a missing boundary as IOException, not InvalidDataException.
        // Match its specific EOF diagnostic; transport/storage I/O failures must remain server errors.
        catch (IOException exception) when (
            !cancellationToken.IsCancellationRequested
            && exception.Source == "Microsoft.AspNetCore.WebUtilities"
            && exception.Message == "Unexpected end of Stream, the content may have already been read by another component. ")
        {
            return ApiProblems.BadRequest("multipart 请求体无效。");
        }

        if (form.Keys.Any(key => key is not ("name" or "platforms" or "profile")))
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
                "配置名必须为 1～100 个 Unicode 字符、最多 255 个 UTF-8 字节，且必须是有效的跨平台目录名。");
        }

        if (!ApiRequestValidation.TryParsePlatforms(
                form["platforms"],
                out var platforms))
        {
            return ApiProblems.BadRequest(
                "平台必须非空、有效且不重复。");
        }

        JsonObject? profile = null;
        if (form.TryGetValue("profile", out var profileValues))
        {
            if (profileValues.Count != 1)
            {
                return ApiProblems.BadRequest("profile 必须且只能提供一个 JSON 对象。");
            }

            try
            {
                profile = JsonNode.Parse(profileValues[0] ?? string.Empty) as JsonObject;
            }
            catch (JsonException)
            {
                return ApiProblems.BadRequest("profile 必须是有效的 JSON 对象。");
            }

            if (profile is null)
            {
                return ApiProblems.BadRequest("profile 必须是有效的 JSON 对象。");
            }
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
            int preambleLength = yamlBytes.AsSpan().StartsWith("\uFEFF"u8)
                ? 3
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

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return ApiProblems.BadRequest("yaml 不能为空。");
        }

        try
        {
            ConversionBundle bundle = await engine.ConvertAsync(
                new ConversionRequest(name, yaml, platforms) { Profile = profile },
                cancellationToken);
            byte[] archive = await ConversionZipBuilder.BuildAsync(
                bundle,
                cancellationToken);

            context.Response.Headers.ContentDisposition =
                "attachment; filename=\"boxforge-output.zip\"";
            return Results.File(archive, "application/zip");
        }
        catch (FormatException)
        {
            return ApiProblems.BadRequest("profile 必须是有效的配置覆盖对象。");
        }
        catch (BoxForgeConversionException)
        {
            return ApiProblems.ConversionFailed();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiProblems.UnexpectedError(context, exception);
        }
    }

    private static bool IsMultipartFormData(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && parsed.MediaType.Equals(
            "multipart/form-data",
            StringComparison.OrdinalIgnoreCase);

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
