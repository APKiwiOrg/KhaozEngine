using System;
using System.Buffers;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// One chunk's rows on their way to <see cref="ContentChunkCodec.Encode"/>. Every body lands in ONE growable
/// arena and the row list is built from offsets at the end, so a chunk of a thousand rows is one buffer
/// rather than a thousand.
/// <para>
/// It is itself the <see cref="IBufferWriter{T}"/> a row codec encodes into, which is what keeps the body out
/// of an intermediate copy: <see cref="Add(ContentRow, IContentRowCodec)"/> records where the arena stood,
/// hands the assembler to the codec, and takes the length from where the arena ended up.
/// </para>
/// <para>
/// The id and the retired bit are taken from the ROW, because the chunk's row table is what carries them and
/// the row body does not (spec 7.3). Ordering is the caller's: the publisher sorts ascending by id before it
/// encodes (spec 6.7), and <see cref="ContentChunkCodec.Encode"/> is what refuses a set that is not sorted.
/// </para>
/// </summary>
public sealed class ContentChunkAssembler : IBufferWriter<byte>
{
    /// <summary>The arena's first size, which covers a typical chunk of small rows without a resize.</summary>
    const int InitialArenaBytes = 64 * 1024;

    readonly List<ContentChunkRow> _rows = new(1024);
    byte[] _arena = new byte[InitialArenaBytes];
    int _used;
    int _largestRowBytes;

    /// <summary>The rows committed so far.</summary>
    public int RowCount => _rows.Count;

    /// <summary>The arena bytes committed so far, which is the uncompressed size of the row bodies alone.</summary>
    public int BodyBytes => _used;

    /// <summary>
    /// The largest committed row body, which is what <c>KEC0026</c> is checked against: a type's own row cap
    /// bounds one ROW, and the registration bound of spec 7.1 is what relates that cap to the chunk ceiling.
    /// </summary>
    public int LargestRowBytes => _largestRowBytes;

    /// <summary>Clears the rows and rewinds the arena, keeping the buffer for the next chunk.</summary>
    public void Reset()
    {
        _rows.Clear();
        _used = 0;
        _largestRowBytes = 0;
    }

    /// <summary>
    /// Encodes one row into the arena and commits it, taking the definition id and the retired bit from the
    /// row itself.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void Add(ContentRow row, IContentRowCodec codec)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(codec);

        int start = _used;
        codec.Encode(row, this);
        Commit(row.Id, row.IsRetired, start, _used - start);
    }

    /// <summary>Commits a body that is already encoded, copying it into the arena.</summary>
    public void Add(int definitionId, bool isRetired, ReadOnlySpan<byte> body)
    {
        int start = _used;
        body.CopyTo(GetSpan(body.Length));
        _used += body.Length;
        Commit(definitionId, isRetired, start, body.Length);
    }

    /// <summary>
    /// The committed rows, each pointing at its slice of the arena.
    /// <para>
    /// The slices are over the arena AS IT STANDS, so a later <see cref="Add(ContentRow, IContentRowCodec)"/>
    /// that grows the arena leaves an already-built list pointing at the old buffer. Build once, at the end
    /// of a chunk, which is the only order a publisher has any reason to use.
    /// </para>
    /// </summary>
    public IReadOnlyList<ContentChunkRow> Build()
    {
        return _rows.ToArray();
    }

    /// <inheritdoc />
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _arena.Length - _used);
        _used += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _arena.AsMemory(_used);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _arena.AsSpan(_used);
    }

    void Commit(int definitionId, bool isRetired, int start, int length)
    {
        _rows.Add(new ContentChunkRow(definitionId, isRetired, _arena.AsMemory(start, length)));
        if (length > _largestRowBytes)
        {
            _largestRowBytes = length;
        }
    }

    void Reserve(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        int wanted = sizeHint == 0 ? 1 : sizeHint;
        if (_arena.Length - _used >= wanted)
        {
            return;
        }

        long size = _arena.Length;
        while (size - _used < wanted)
        {
            size *= 2;
        }

        Array.Resize(ref _arena, (int)Math.Min(size, Array.MaxLength));
    }
}
