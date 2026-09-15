using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// One content type's row codec: the only thing that turns a <see cref="ContentRow"/> into canonical bytes
/// and back.
/// <para>
/// <see cref="TryDecode"/> is TOTAL. The bytes arrive from a remote peer, so it returns false with a stable
/// reason token from the fixed list rather than throwing, and it never half-builds a row.
/// </para>
/// <para>
/// <see cref="WrittenFields"/> is checked against the type's schema at registration (contracts 4.7), which
/// is what stops an editor writing a field nothing reads. A derived marker is not written by anyone, so it
/// belongs in the schema and not in this list.
/// </para>
/// </summary>
public interface IContentRowCodec
{
    /// <summary>The schema field names this codec writes, compared as a SET at registration.</summary>
    IReadOnlyList<string> WrittenFields { get; }

    /// <summary>Writes the row's canonical bytes.</summary>
    void Encode(ContentRow row, IBufferWriter<byte> destination);

    /// <summary>Reads one row body. Returns false with a reason token rather than throwing.</summary>
    bool TryDecode(ReadOnlySpan<byte> body, [MaybeNullWhen(false)] out ContentRow row, out string? reason);
}
