using System;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// A content key as the runtime holds it, spec section 9.1: a slice of the loaded UTF-8 blob rather than a
/// string. Every row body opens with its own key, length prefixed, so the blob is the type's
/// <c>Bodies</c> array and a key costs no bytes of its own. A string is materialised only on demand, for a
/// log line, a console response or a validator finding.
/// </summary>
public readonly struct ContentKey : IEquatable<ContentKey>
{
    private readonly byte[]? _blob;
    private readonly int _start;
    private readonly int _length;

    public ContentKey(byte[] blob, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(blob);
        _blob = blob;
        _start = start;
        _length = length;
    }

    /// <summary>A key handed in as a string, encoded once at construction so the compare stays ordinal bytes.</summary>
    public ContentKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _blob = Encoding.UTF8.GetBytes(key);
        _start = 0;
        _length = _blob.Length;
    }

    public ReadOnlySpan<byte> Utf8 => _blob is null ? default : _blob.AsSpan(_start, _length);

    public bool IsEmpty => _length == 0;

    public bool Equals(ContentKey other) => Utf8.SequenceEqual(other.Utf8);

    public override bool Equals(object? obj) => obj is ContentKey other && Equals(other);

    public override int GetHashCode() => (int)ContentKeyHash.Of(Utf8);

    public override string ToString() => _blob is null || _length == 0 ? string.Empty : Encoding.UTF8.GetString(Utf8);

    public static bool operator ==(ContentKey left, ContentKey right) => left.Equals(right);

    public static bool operator !=(ContentKey left, ContentKey right) => !left.Equals(right);
}

/// <summary>
/// The hash and the capacity rule both open-addressed key indexes share: the per-type <c>KeyIds</c> table of
/// section 9.1 and the per-language text index of section 7.6. FNV-1a with a final avalanche, so masking to
/// the low bits carries the whole key rather than its tail.
/// </summary>
public static class ContentKeyHash
{
    public static uint Of(ReadOnlySpan<byte> key)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= 16777619u;
        }
        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        return hash;
    }

    /// <summary>A power of two at least twice the entry count, so a probe walk stays short.</summary>
    public static int CapacityFor(int entries)
    {
        long wanted = Math.Max(16L, (long)entries * 2);
        int capacity = 16;
        while (capacity < wanted && capacity < (1 << 30)) capacity <<= 1;
        return capacity;
    }
}
