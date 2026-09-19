using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.AzureBlob;

namespace KhaozEngine.Tests.Catalog.AzureBlob;

/// <summary>
/// The container seam in memory: a dictionary of key to bytes that also records the headers each object was
/// written with, how many uploads actually WROTE, and whether a body was ever materialized. Those counters
/// are what turn "uploads if absent" and "refuses an oversize object before buffering it" into facts rather
/// than into descriptions of the code.
/// <para>
/// It is deliberately not a simulation of a blob container. The only two behaviours it reproduces are the
/// ones the store's correctness rests on: a conditional upload that loses a race, and a length the store can
/// read before it asks for a body.
/// </para>
/// </summary>
internal sealed class InMemoryBlobContainer : IBlobContainer
{
    readonly Dictionary<string, BlobRecord> _blobs = new(StringComparer.Ordinal);

    /// <summary>How many upload calls the store made, whether or not they wrote.</summary>
    public int Uploads { get; private set; }

    /// <summary>How many uploads actually wrote an object, which a second put of a hash must not raise.</summary>
    public int Writes { get; private set; }

    /// <summary>How many times a body was built to serve a download, which an oversize refusal must not raise.</summary>
    public int BodiesMaterialized { get; private set; }

    /// <summary>
    /// When set, the NEXT upload finds the object already there: the container stores the offered bytes as
    /// the other writer's and answers that it did not write, which is the conditional upload losing a race.
    /// </summary>
    public bool ConcurrentWriterWins { get; set; }

    /// <summary>Every key the container holds, in insertion order.</summary>
    public IReadOnlyCollection<string> Keys => _blobs.Keys;

    /// <summary>The bytes under one key, or null when it holds none.</summary>
    public byte[]? Read(string key) => _blobs.TryGetValue(key, out BlobRecord record) ? record.Body : null;

    /// <summary>The headers one object was written with.</summary>
    public BlobObjectHeaders HeadersOf(string key) => _blobs[key].Headers;

    /// <summary>
    /// Plants an object that DECLARES a length without holding bytes, so a store that reads the length
    /// first costs nothing and a store that buffers first is caught by <see cref="BodiesMaterialized"/>.
    /// </summary>
    public void PlanOversize(string key, long length) =>
        _blobs[key] = new BlobRecord(null, length, new BlobObjectHeaders("application/octet-stream", "public"));

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_blobs.ContainsKey(key));

    /// <inheritdoc />
    public Task<bool> UploadIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        BlobObjectHeaders headers,
        CancellationToken cancellationToken)
    {
        Uploads++;
        if (ConcurrentWriterWins)
        {
            ConcurrentWriterWins = false;
            _blobs[key] = new BlobRecord(bytes.ToArray(), bytes.Length, headers);
            return Task.FromResult(false);
        }

        if (_blobs.ContainsKey(key))
        {
            return Task.FromResult(false);
        }

        Writes++;
        _blobs[key] = new BlobRecord(bytes.ToArray(), bytes.Length, headers);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> DownloadAsync(
        string key,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!_blobs.TryGetValue(key, out BlobRecord record))
        {
            return Task.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        if (record.Length > maximumBytes)
        {
            return Task.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        if (record.Body is null)
        {
            BodiesMaterialized++;
            return Task.FromResult<ReadOnlyMemory<byte>?>(new byte[record.Length]);
        }

        BodiesMaterialized++;
        return Task.FromResult<ReadOnlyMemory<byte>?>(record.Body);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_blobs.Remove(key));

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListKeysAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (string key in new List<string>(_blobs.Keys))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return key;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    readonly record struct BlobRecord(byte[]? Body, long Length, BlobObjectHeaders Headers);
}
