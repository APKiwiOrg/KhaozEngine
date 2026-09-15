using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>What one cold start cost: the wall clock to every chunk verified, and the local half of it.</summary>
public sealed class FetchOutcome
{
    public required double WallClockMs { get; init; }
    public required double LocalWorkMs { get; init; }
    public required double ManifestMs { get; init; }
    public required double VerifyMs { get; init; }
    public required long BytesFetched { get; init; }
    public required int ChunksFetched { get; init; }
    public required int Failures { get; init; }
    public required long LinkBitsPerSecond { get; init; }
    public required int Concurrency { get; init; }

    /// <summary>The transfer floor the shaped link imposes, which P4b says the design cannot beat.</summary>
    public double TransferFloorMs => BytesFetched * 8.0 * 1000.0 / LinkBitsPerSecond;
}

/// <summary>
/// The client fetch loop of spec section 8.7, timed from the door refusal to every chunk verified: fetch
/// the manifest, verify it, compute the missing set against an empty cache, then fetch with bounded
/// concurrency through the shared token bucket, verifying each chunk's hash as it lands.
/// <para>
/// Decode stays LAZY, per section 9.3. Step 5 stores bytes and verifies a hash; nothing is decoded, which
/// is what makes this a download budget rather than a decode one.
/// </para>
/// </summary>
public static class ClientFetchSimulator
{
    public static async Task<FetchOutcome> RunAsync(
        PackHttpServer server,
        string manifestHash,
        long linkBitsPerSecond,
        int concurrency,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(server);
        var bucket = new TokenBucket(linkBitsPerSecond);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(server.BaseAddress),
            Timeout = TimeSpan.FromMinutes(20),
        };

        var wall = Stopwatch.StartNew();
        var manifestClock = Stopwatch.StartNew();
        byte[] manifestFile = await FetchAsync(client, bucket, manifestHash, cancellation).ConfigureAwait(false);
        if (!ContentManifestCodec.TryDecode(manifestFile, out ContentManifest? manifest, out string reason) || manifest is null)
            throw new InvalidOperationException("Manifest refused: " + reason);
        manifestClock.Stop();

        List<string> missing = manifest.EveryChunkHash();
        var hashes = new Dictionary<string, bool>(missing.Count);
        foreach (ManifestTypeEntry type in manifest.Types)
        {
            foreach (ManifestChunkEntry chunk in type.Chunks) hashes[chunk.Hash] = true;
        }
        foreach (ManifestLanguageEntry language in manifest.Languages) hashes[language.TextHash] = false;

        long bytes = 0;
        int failures = 0;
        double verifyMs = 0;
        var queue = new ConcurrentQueue<string>(missing);
        var workers = new Task[Math.Max(1, concurrency)];
        for (int w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(async () =>
            {
                while (queue.TryDequeue(out string? hash))
                {
                    byte[] file = await FetchAsync(client, bucket, hash, cancellation).ConfigureAwait(false);
                    Interlocked.Add(ref bytes, file.Length);
                    var verify = Stopwatch.StartNew();
                    bool ok = hashes[hash]
                        ? ContentChunkCodec.TryVerify(file, hash, out _)
                        : VerifyText(file, hash);
                    verify.Stop();
                    AddDouble(ref verifyMs, verify.Elapsed.TotalMilliseconds);
                    if (!ok) Interlocked.Increment(ref failures);
                }
            }, cancellation);
        }
        await Task.WhenAll(workers).ConfigureAwait(false);
        wall.Stop();

        return new FetchOutcome
        {
            WallClockMs = wall.Elapsed.TotalMilliseconds,
            LocalWorkMs = manifestClock.Elapsed.TotalMilliseconds + verifyMs,
            ManifestMs = manifestClock.Elapsed.TotalMilliseconds,
            VerifyMs = verifyMs,
            BytesFetched = bytes + manifestFile.Length,
            ChunksFetched = missing.Count,
            Failures = failures,
            LinkBitsPerSecond = linkBitsPerSecond,
            Concurrency = concurrency,
        };
    }

    private static bool VerifyText(ReadOnlySpan<byte> file, string expectedHash)
    {
        if (file.Length < ContentPackFormat.TextHeaderFixedBytes + 1) return false;
        int tagLength = file[6];
        int headerBytes = ContentPackFormat.TextHeaderFixedBytes + tagLength;
        if (file.Length < headerBytes) return false;
        uint uncompressed = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file[(9 + tagLength)..]);
        if (uncompressed > ContentPackFormat.MaxChunkUncompressedBytes) return false;
        byte[] header = file[..headerBytes].ToArray();
        header[7 + tagLength] = ContentPackFormat.CompressionNone;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(13 + tagLength), uncompressed);
        byte[] body = new byte[uncompressed];
        if (file[7 + tagLength] == ContentPackFormat.CompressionNone)
        {
            file[headerBytes..].CopyTo(body);
        }
        else if (!System.IO.Compression.BrotliDecoder.TryDecompress(file[headerBytes..], body, out int written)
                 || written != uncompressed)
        {
            return false;
        }
        return string.Equals(
            ContentHash.OfChunkStreaming(header, body, ContentHash.TextDomain),
            expectedHash,
            StringComparison.Ordinal);
    }

    private static async Task<byte[]> FetchAsync(HttpClient client, TokenBucket bucket, string hash, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await client
            .GetAsync(hash + ".kec", HttpCompletionOption.ResponseHeadersRead, cancellation)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        long? length = response.Content.Headers.ContentLength;
        var buffer = new System.IO.MemoryStream(length is > 0 ? (int)length.Value : 64 * 1024);
        byte[] block = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(block, cancellation).ConfigureAwait(false);
            if (read == 0) break;
            await bucket.ConsumeAsync(read, cancellation).ConfigureAwait(false);
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private static void AddDouble(ref double target, double value)
    {
        double seen = Volatile.Read(ref target);
        while (true)
        {
            double updated = seen + value;
            double previous = Interlocked.CompareExchange(ref target, updated, seen);
            if (previous.Equals(seen)) return;
            seen = previous;
        }
    }
}
