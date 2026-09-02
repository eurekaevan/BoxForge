namespace BoxForge.Server.Api;

internal static class SecurityHeaders
{
    private const string ContentSecurityPolicy =
        "default-src 'none'; "
        + "script-src 'self'; "
        + "style-src 'self'; "
        + "connect-src 'self'; "
        + "img-src 'self' data:; "
        + "base-uri 'none'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'; "
        + "object-src 'none'";

    public static async Task ApplyAsync(
        HttpContext context,
        RequestDelegate next)
    {
        IHeaderDictionary headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";

        await next(context);
    }
}
