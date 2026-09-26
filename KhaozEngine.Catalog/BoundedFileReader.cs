using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>Reads one file under a fixed byte ceiling before allocating its contents.</summary>
internal static class BoundedFileReader
{
    public static async Task<ReadOnlyMemory<byte>?> ReadAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAsync(stream, stream.Length, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ReadOnlyMemory<byte>?> ReadAsync(
        Stream stream,
        long observedLength,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (observedLength > maxBytes)
        {
            return null;
        }

        byte[] bytes = new byte[(int)observedLength];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

        byte[] probe = new byte[1];
        if (await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
        {
            return null;
        }

        return new ReadOnlyMemory<byte>(bytes);
    }
}
