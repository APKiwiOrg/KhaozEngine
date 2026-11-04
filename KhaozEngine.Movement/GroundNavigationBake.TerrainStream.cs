using System;
using System.Buffers;
using System.IO;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigationBake
{
    // A non-seekable truncated stream must not allocate its advertised payload length.
    static byte[]? ReadTerrainPayload(Stream source, int length)
    {
        using var output = new MemoryStream(Math.Min(length, 65536));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int remaining = length;
            while (remaining > 0)
            {
                int read = source.Read(buffer.AsSpan(0, Math.Min(remaining, buffer.Length)));
                if (read == 0) return null;
                output.Write(buffer.AsSpan(0, read)); remaining -= read;
            }
            return output.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
