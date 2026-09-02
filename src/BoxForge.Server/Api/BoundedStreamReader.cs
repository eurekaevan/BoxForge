using System.Buffers;

namespace BoxForge.Server.Api;

internal static class BoundedStreamReader
{
    public static async Task<byte[]?> ReadAsync(
        Stream source,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        using var content = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (content.Length <= maxBytes)
            {
                int remaining = (int)Math.Min(
                    buffer.Length,
                    (long)maxBytes + 1 - content.Length);
                int read = await source.ReadAsync(
                    buffer.AsMemory(0, remaining),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await content.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }

            return content.Length > maxBytes ? null : content.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
