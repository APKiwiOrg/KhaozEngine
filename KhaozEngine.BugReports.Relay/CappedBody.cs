using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Buffers a request body under its intake cap without trusting the declared length.</summary>
internal static class CappedBody
{
    public static async Task<MemoryStream?> ReadAsync(Stream body, long? declaredLength, long maxBytes, CancellationToken ct)
    {
        if (declaredLength > maxBytes) return null;
        var output = new MemoryStream();
        try
        {
            byte[] buffer = new byte[(int)Math.Min(81920, maxBytes + 1)];
            while (output.Length <= maxBytes)
            {
                int count = (int)Math.Min(buffer.Length, maxBytes + 1 - output.Length);
                int read = await body.ReadAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    output.Position = 0;
                    return output;
                }
                output.Write(buffer, 0, read);
            }
        }
        catch
        {
            output.Dispose();
            throw;
        }
        output.Dispose();
        return null;
    }
}
