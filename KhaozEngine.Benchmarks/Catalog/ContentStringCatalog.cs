using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// One language of the text chunks of spec section 7.6, resident the way section 7.6 says a decoded
/// language is held: the decompressed chunk BODY plus one open-addressed index of entry offsets over it,
/// and no decoded entry at all. Nothing in the chunk exists as UTF-16 until something asks for it.
/// <para>
/// <c>IStringCatalog.Get</c> returns a <c>string</c>, so a materialisation happens somewhere, and the only
/// choice is whether the same one happens twice. It happens through a bounded direct-mapped cache of 512
/// resolved values, which is about 80 KB at a 60-character value and is bounded by construction rather
/// than by a policy.
/// </para>
/// </summary>
public sealed class ContentStringCatalog
{
    /// <summary>Direct-mapped, a power of two, and the whole reason the resident figure has a ceiling.</summary>
    public const int CacheEntries = 512;

    private readonly Shard[] _shards;
    private readonly string?[] _cacheValues = new string?[CacheEntries];
    private readonly long[] _cacheTags = new long[CacheEntries];

    private ContentStringCatalog(Shard[] shards)
    {
        _shards = shards;
        int entries = 0;
        foreach (Shard shard in shards) entries += shard.Count;
        EntryCount = entries;
    }

    public int EntryCount { get; }

    public int ShardCount => _shards.Length;

    public int CacheMisses { get; private set; }

    /// <summary>
    /// Builds the resident form from the stored chunk files of one language, in any order: the shards are
    /// sorted by their first key here, because a manifest orders language entries by tag and an ordinal tag
    /// order is not a shard order once a language passes ten shards.
    /// </summary>
    public static bool TryBuild(IReadOnlyList<byte[]> files, out ContentStringCatalog? catalog, out string reason)
    {
        ArgumentNullException.ThrowIfNull(files);
        catalog = null;
        var shards = new List<Shard>(files.Count);
        foreach (byte[] file in files)
        {
            if (!ContentTextChunkCodec.TryDecodeBody(file, out byte[]? body, out reason) || body is null) return false;
            if (!Shard.TryBuild(body, out Shard? shard, out reason) || shard is null) return false;
            shards.Add(shard);
        }
        if (shards.Count == 0) { reason = "text-no-shard"; return false; }
        shards.Sort(static (left, right) => left.FirstKey.SequenceCompareTo(right.FirstKey));
        catalog = new ContentStringCatalog(shards.ToArray());
        reason = string.Empty;
        return true;
    }

    /// <summary>The value as a slice of the shard body. No allocation, and this is what the index resolves to.</summary>
    public bool TryGetUtf8(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        int shardIndex = ShardFor(key);
        if (_shards[shardIndex].TryFind(key, out int record))
        {
            value = _shards[shardIndex].Value(record);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>The `IStringCatalog.Get` shape, through the bounded cache. A miss materialises once.</summary>
    public bool TryGet(ReadOnlySpan<byte> key, out string value)
    {
        int shardIndex = ShardFor(key);
        Shard shard = _shards[shardIndex];
        if (!shard.TryFind(key, out int record))
        {
            value = string.Empty;
            return false;
        }
        long tag = ((long)shardIndex << 32) | (uint)(record + 1);
        int slot = (int)((ulong)tag * 0x9E3779B97F4A7C15UL >> 55) & (CacheEntries - 1);
        if (_cacheTags[slot] == tag)
        {
            value = _cacheValues[slot]!;
            return true;
        }
        value = Encoding.UTF8.GetString(shard.Value(record));
        _cacheTags[slot] = tag;
        _cacheValues[slot] = value;
        CacheMisses++;
        return true;
    }

    /// <summary>The same lookup with the cache bypassed, which is the per-call alternative section 7.6 weighed.</summary>
    public bool TryGetUncached(ReadOnlySpan<byte> key, out string value)
    {
        if (TryGetUtf8(key, out ReadOnlySpan<byte> utf8))
        {
            value = Encoding.UTF8.GetString(utf8);
            return true;
        }
        value = string.Empty;
        return false;
    }

    /// <summary>A sample of live keys, copied out for a probe set. Harness only, never a runtime path.</summary>
    public List<byte[]> SampleKeys(int count)
    {
        var keys = new List<byte[]>(count);
        if (count <= 0 || EntryCount == 0) return keys;
        int stride = Math.Max(1, EntryCount / count);
        int taken = 0;
        foreach (Shard shard in _shards)
        {
            foreach (int record in shard.Records())
            {
                if (taken++ % stride != 0) continue;
                keys.Add(shard.Key(record).ToArray());
                if (keys.Count >= count) return keys;
            }
        }
        return keys;
    }

    /// <summary>What this catalog retains, for the resident line of budget P10.</summary>
    public long ApproximateBytes()
    {
        long bytes = (CacheEntries * 8L) + (CacheEntries * 8L);
        foreach (Shard shard in _shards) bytes += shard.Body.LongLength + ((long)shard.Buckets.Length * 4);
        return bytes;
    }

    /// <summary>
    /// Which shard holds a key. The entries are ordinal ascending across the whole language, so the shards
    /// partition that order and a binary search over each shard's first key picks one in four comparisons at
    /// twelve shards. UTF-8 byte order and ordinal string order agree on these keys, which are ASCII.
    /// </summary>
    private int ShardFor(ReadOnlySpan<byte> key)
    {
        int low = 0;
        int high = _shards.Length - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (_shards[mid].FirstKey.SequenceCompareTo(key) <= 0) low = mid;
            else high = mid - 1;
        }
        return low;
    }

    /// <summary>One text chunk as it is held: its body, and the open-addressed offset index over it.</summary>
    private sealed class Shard
    {
        private Shard(byte[] body, int[] buckets, int count, int firstRecord)
        {
            Body = body;
            Buckets = buckets;
            Count = count;
            FirstKey = count == 0 ? [] : KeyOf(body, firstRecord).ToArray();
        }

        public byte[] Body { get; }

        /// <summary>An entry's record offset plus one in an occupied bucket, 0 in an empty one.</summary>
        public int[] Buckets { get; }

        public int Count { get; }

        public byte[] FirstKey { get; }

        public static bool TryBuild(byte[] body, out Shard? shard, out string reason)
        {
            shard = null;
            int cursor = 0;
            if (!ContentVarint.TryRead(body, ref cursor, out uint entryCount)) { reason = "text-entry-count"; return false; }
            int capacity = ContentKeyHash.CapacityFor((int)entryCount);
            int[] buckets = new int[capacity];
            int mask = capacity - 1;
            int firstRecord = cursor;
            for (uint i = 0; i < entryCount; i++)
            {
                int record = cursor;
                if (cursor >= body.Length) { reason = "text-truncated-entry"; return false; }
                int keyLength = body[cursor++];
                if (keyLength is < 1 or > ContentTextChunkCodec.MaxKeyBytes) { reason = "text-key-length"; return false; }
                if (cursor + keyLength > body.Length) { reason = "text-truncated-entry"; return false; }
                ReadOnlySpan<byte> key = body.AsSpan(cursor, keyLength);
                cursor += keyLength;
                if (!ContentVarint.TryRead(body, ref cursor, out uint valueLength)) { reason = "text-value-length"; return false; }
                if (valueLength > ContentTextChunkCodec.MaxValueBytes) { reason = "text-value-length"; return false; }
                if (cursor + (int)valueLength > body.Length) { reason = "text-truncated-entry"; return false; }
                cursor += (int)valueLength;

                int slot = (int)(ContentKeyHash.Of(key) & (uint)mask);
                while (buckets[slot] != 0)
                {
                    if (KeyOf(body, buckets[slot] - 1).SequenceEqual(key)) break;
                    slot = (slot + 1) & mask;
                }
                buckets[slot] = record + 1;
            }
            if (cursor != body.Length) { reason = "text-trailing-bytes"; return false; }
            shard = new Shard(body, buckets, (int)entryCount, firstRecord);
            reason = string.Empty;
            return true;
        }

        public bool TryFind(ReadOnlySpan<byte> key, out int record)
        {
            int mask = Buckets.Length - 1;
            int slot = (int)(ContentKeyHash.Of(key) & (uint)mask);
            while (true)
            {
                int candidate = Buckets[slot];
                if (candidate == 0)
                {
                    record = -1;
                    return false;
                }
                if (KeyOf(Body, candidate - 1).SequenceEqual(key))
                {
                    record = candidate - 1;
                    return true;
                }
                slot = (slot + 1) & mask;
            }
        }

        public ReadOnlySpan<byte> Key(int record) => KeyOf(Body, record);

        public ReadOnlySpan<byte> Value(int record)
        {
            int cursor = record;
            int keyLength = Body[cursor++];
            cursor += keyLength;
            ContentVarint.TryRead(Body, ref cursor, out uint valueLength);
            return Body.AsSpan(cursor, (int)valueLength);
        }

        /// <summary>Every record offset in stored order, which is ordinal ascending by key.</summary>
        public IEnumerable<int> Records()
        {
            int cursor = 0;
            ContentVarint.TryRead(Body, ref cursor, out uint entryCount);
            for (uint i = 0; i < entryCount; i++)
            {
                int record = cursor;
                int keyLength = Body[cursor++];
                cursor += keyLength;
                ContentVarint.TryRead(Body, ref cursor, out uint valueLength);
                cursor += (int)valueLength;
                yield return record;
            }
        }

        private static ReadOnlySpan<byte> KeyOf(byte[] body, int record) => body.AsSpan(record + 1, body[record]);
    }
}
