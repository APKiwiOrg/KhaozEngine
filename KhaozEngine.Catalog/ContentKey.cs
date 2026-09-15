using System;
using System.Text;

namespace KhaozEngine.Catalog;

/// <summary>
/// A content row's string key as the runtime holds it: a slice of the loaded UTF-8 blob rather than a
/// string. Every row body opens with its own key, length prefixed, so the blob is the type's row storage
/// and a key costs no bytes of its own. A string is materialised only ON DEMAND, for a log line, a console
/// response or a validator finding.
/// <para>
/// Comparison is ORDINAL, always, never culture aware and never case insensitive, which is why both
/// authoring providers pin their key columns to a binary collation: a case-insensitive database default
/// silently merged two rows once.
/// </para>
/// <para>
/// The character set and the 64 character cap of the key rules are the VALIDATOR's, not this type's. An
/// over-long or malformed key therefore reaches the validator intact and is reported as a finding, rather
/// than being silently truncated or thrown on at construction, which is what lets a bulk import report
/// every bad key in one pass instead of the first.
/// </para>
/// </summary>
public readonly struct ContentKey : IEquatable<ContentKey>
{
    readonly byte[]? _blob;
    readonly int _start;
    readonly int _length;

    /// <summary>A key that is a slice of a larger blob, which is how a loaded row hands one out.</summary>
    public ContentKey(byte[] blob, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + (long)length, blob.Length);
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

    /// <summary>The key's bytes, which is what every comparison and every digest reads.</summary>
    public ReadOnlySpan<byte> Utf8 => _blob is null ? default : _blob.AsSpan(_start, _length);

    /// <summary>True for a default key and for a zero-length slice, which compare equal to each other.</summary>
    public bool IsEmpty => _length == 0;

    /// <inheritdoc />
    public bool Equals(ContentKey other) => Utf8.SequenceEqual(other.Utf8);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContentKey other && Equals(other);

    /// <summary>
    /// FNV-1a over the key's bytes with a final avalanche, so masking to the low bits carries the whole key
    /// rather than its tail. Over the BYTES rather than a materialised string, so a key sliced out of a
    /// loaded blob and the same key built from a string land in the same dictionary bucket.
    /// </summary>
    public override int GetHashCode()
    {
        ReadOnlySpan<byte> bytes = Utf8;
        uint hash = 2166136261u;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 16777619u;
        }

        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        return (int)hash;
    }

    /// <summary>Materialises the key as a string. Allocates, so this is the log and finding path, not the hot one.</summary>
    public override string ToString() => _length == 0 ? string.Empty : Encoding.UTF8.GetString(Utf8);

    /// <summary>Ordinal byte equality.</summary>
    public static bool operator ==(ContentKey left, ContentKey right) => left.Equals(right);

    /// <summary>Ordinal byte inequality.</summary>
    public static bool operator !=(ContentKey left, ContentKey right) => !left.Equals(right);
}
