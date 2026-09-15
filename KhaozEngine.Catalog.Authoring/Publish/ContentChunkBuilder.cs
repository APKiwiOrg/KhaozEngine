using System;
using System.Buffers;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// How one row's body is written FOR ONE SIDE, which is the seam the client encode of spec 6.7 lives behind.
/// <para>
/// It exists as a seam for one reason: <c>KEC0014</c>'s trigger is an ENCODER defect rather than an
/// authoring one, so the only way to exercise the check is to hand the publisher an encoder that leaves a
/// <c>ServerOnly</c> field in the client bytes. A check nothing can make fire is a check nobody can trust.
/// </para>
/// </summary>
public interface IContentRowSideEncoder
{
    /// <summary>Writes one row's canonical bytes as the given side carries them.</summary>
    /// <param name="type">The registration whose codec and schema drive the write.</param>
    /// <param name="row">The row, with every field the server holds.</param>
    /// <param name="side">The side being encoded.</param>
    /// <param name="destination">Where the bytes go.</param>
    void Encode(ContentTypeRegistration type, ContentRow row, ContentVisibility side, IBufferWriter<byte> destination);
}

/// <summary>
/// The engine's own side encoder: the server side is the row as authored, and the CLIENT side is the same
/// row with every <c>ServerOnly</c> field omitted (spec 6.7, contracts 11.3).
/// <para>
/// <b>Omitted means written as ABSENT rather than skipped.</b> A row body is a POSITIONAL walk over the
/// type's schema, so a field that took no width would shift every field after it and a client decoding the
/// chunk would read the wrong values rather than fewer of them. The absence convention writes the zero form
/// of the field's kind, one byte in every case, and the field comes back absent on the client.
/// </para>
/// <para>
/// A type with no <c>ServerOnly</c> field encodes identically on both sides, so nothing is copied and
/// nothing is masked for the ordinary case.
/// </para>
/// </summary>
public sealed class ContentSideRowEncoder : IContentRowSideEncoder
{
    /// <summary>The one instance. It holds no state, so a publisher never needs its own.</summary>
    public static ContentSideRowEncoder Default { get; } = new();

    /// <summary>True when the type's schema marks at least one field <c>ServerOnly</c>.</summary>
    /// <param name="type">The registration to read the schema of.</param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    public static bool HasServerOnlyField(ContentTypeRegistration type)
    {
        ArgumentNullException.ThrowIfNull(type);
        IReadOnlyList<ContentFieldEntry> fields = type.Schema.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].Visibility == ContentVisibility.ServerOnly)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void Encode(
        ContentTypeRegistration type,
        ContentRow row,
        ContentVisibility side,
        IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(row);

        if (side != ContentVisibility.Client || !HasServerOnlyField(type))
        {
            type.Codec.Encode(row, destination);
            return;
        }

        type.Codec.Encode(Masked(type, row), destination);
    }

    static ContentRow Masked(ContentTypeRegistration type, ContentRow row)
    {
        IReadOnlyList<ContentFieldEntry> fields = type.Schema.Fields;
        var values = new ContentFieldValue[row.Fields.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i < fields.Count && fields[i].Visibility == ContentVisibility.ServerOnly
                ? ContentFieldValue.Absent(fields[i].Kind)
                : row.Fields[i];
        }

        return new ContentRow(row.Type, row.Id, row.Key, row.ParentId, row.IsRetired, values);
    }
}

/// <summary>
/// Steps 6 and 7 of spec 6.1: which chunks this version touches, and the bytes, the hash and the stored file
/// of each one it does.
/// <para>
/// <b>Every chunk NOT in the affected set keeps its previous version's hash.</b> It is not encoded, not
/// compressed, not hashed and not written, which is the mechanism behind the owner's
/// download-size-after-a-one-item-edit budget and the reason chunk identity had to be an id range rather
/// than a row range.
/// </para>
/// <para>
/// <b>The carry forward is PER SIDE.</b> For an unaffected chunk the publisher copies forward every chunk
/// row the previous version holds for that <c>(typeId, chunkIndex)</c>, one for a single-sided chunk and two
/// when the type is <c>Client</c> with a per-field <c>ServerOnly</c> override. Nothing recomputes a side
/// from the type's default visibility, because the schema may have gained a <c>ServerOnly</c> field at THIS
/// version and the previous row set is what says which sides that version actually wrote.
/// </para>
/// </summary>
static class ContentChunkBuilder
{
    /// <summary>
    /// The sides a type PRODUCES. A <c>ServerOnly</c> type produces one server chunk and no client chunk at
    /// all. A <c>Client</c> type produces one client chunk, plus a server chunk whenever its schema marks a
    /// field <c>ServerOnly</c>, because then the two sides are different bytes.
    /// </summary>
    public static ContentVisibility[] SidesOf(ContentTypeRegistration type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.DefaultVisibility == ContentVisibility.ServerOnly)
        {
            return [ContentVisibility.ServerOnly];
        }

        return ContentSideRowEncoder.HasServerOnlyField(type)
            ? [ContentVisibility.Client, ContentVisibility.ServerOnly]
            : [ContentVisibility.Client];
    }

    /// <summary>
    /// Step 6's affected set: every chunk holding a row that ENTERED at this version or was CLOSED in it.
    /// <para>
    /// Both halves matter. A row that entered changes its chunk, and a row that was closed changes its
    /// chunk too, because it leaves the live set. An update touches the same chunk twice and the set
    /// collapses it to one.
    /// </para>
    /// </summary>
    public static SortedSet<(ushort TypeId, int ChunkIndex)> Affected(
        ContentTypeRegistry registry,
        IReadOnlyList<ContentRowClose> closes,
        IReadOnlyList<ContentRowInsert> inserts)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentNullException.ThrowIfNull(inserts);

        var affected = new SortedSet<(ushort TypeId, int ChunkIndex)>();
        for (int i = 0; i < closes.Count; i++)
        {
            Add(affected, registry, closes[i].Type, closes[i].DefinitionId);
        }

        for (int i = 0; i < inserts.Count; i++)
        {
            Add(affected, registry, inserts[i].Row.Type, inserts[i].Row.Id);
        }

        return affected;
    }

    /// <summary>
    /// Step 7, then step 6's carry forward: encodes every affected chunk at every side its type produces,
    /// and copies forward every chunk row the base version holds for a chunk this version did not touch.
    /// </summary>
    /// <param name="registry">The registry the id ranges and the codecs come from.</param>
    /// <param name="rows">Every row LIVE at this version.</param>
    /// <param name="affected">The affected set.</param>
    /// <param name="baseline">The base version, whose unaffected chunk rows are carried forward.</param>
    /// <param name="encoder">The side encoder, which is what omits a <c>ServerOnly</c> field on the client.</param>
    /// <param name="findings">Where <c>KEC0014</c> lands.</param>
    /// <returns>Every chunk row this version holds, ascending by type, chunk index and side.</returns>
    public static List<ContentChunkRecord> Build(
        ContentTypeRegistry registry,
        IReadOnlyList<ContentCandidateRow> rows,
        SortedSet<(ushort TypeId, int ChunkIndex)> affected,
        ContentPublishBaseline baseline,
        IContentRowSideEncoder encoder,
        ICollection<ContentFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(affected);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(findings);

        Dictionary<(ushort, int), List<ContentCandidateRow>> live = GroupByChunk(rows, affected);
        var records = new List<ContentChunkRecord>();
        var assembler = new ContentChunkAssembler();
        var body = new ArrayBufferWriter<byte>();

        foreach ((ushort typeId, int chunkIndex) in affected)
        {
            ContentTypeRegistration registration = RequireType(registry, typeId);
            List<ContentCandidateRow> chunkRows = live.TryGetValue((typeId, chunkIndex), out List<ContentCandidateRow>? held)
                ? held
                : [];

            // Ascending by id, ALWAYS, because contracts 4.3 requires two processes registering the same
            // types in different orders to produce byte-identical packs.
            chunkRows.Sort(static (left, right) => left.DefinitionId.CompareTo(right.DefinitionId));

            foreach (ContentVisibility side in SidesOf(registration))
            {
                records.Add(Encode(registry, registration, chunkIndex, side, chunkRows, assembler, body, encoder, findings));
            }
        }

        CarryForward(baseline, affected, records);
        records.Sort(static (left, right) =>
        {
            int byType = left.Type.Value.CompareTo(right.Type.Value);
            if (byType != 0)
            {
                return byType;
            }

            int byIndex = left.ChunkIndex.CompareTo(right.ChunkIndex);
            return byIndex != 0 ? byIndex : ((int)left.Side).CompareTo((int)right.Side);
        });
        return records;
    }

    static ContentChunkRecord Encode(
        ContentTypeRegistry registry,
        ContentTypeRegistration registration,
        int chunkIndex,
        ContentVisibility side,
        List<ContentCandidateRow> chunkRows,
        ContentChunkAssembler assembler,
        ArrayBufferWriter<byte> body,
        IContentRowSideEncoder encoder,
        ICollection<ContentFinding> findings)
    {
        // The assembler's rows alias its arena, so the chunk is ENCODED before the next Reset rewinds it.
        assembler.Reset();
        for (int i = 0; i < chunkRows.Count; i++)
        {
            ContentCandidateRow row = chunkRows[i];
            body.ResetWrittenCount();
            encoder.Encode(registration, row.ToRow(), side, body);
            assembler.Add(row.DefinitionId, row.IsRetired, body.WrittenSpan);
        }

        EncodedContentChunk encoded = ContentChunkCodec.Encode(registration, chunkIndex, side, assembler.Build());
        if (side == ContentVisibility.Client && ContentSideRowEncoder.HasServerOnlyField(registration))
        {
            ContentClientEncodeCheck.Run(registry, registration, encoded, findings);
        }

        return new ContentChunkRecord(
            registration.Type,
            chunkIndex,
            side,
            encoded.Hash,
            encoded.UncompressedBytes,
            encoded.StoredBytes,
            encoded.RowCount,
            encoded.StoredFile,
            isReused: false);
    }

    static void CarryForward(
        ContentPublishBaseline baseline,
        SortedSet<(ushort TypeId, int ChunkIndex)> affected,
        List<ContentChunkRecord> records)
    {
        for (int i = 0; i < baseline.Chunks.Count; i++)
        {
            ContentChunkRecord held = baseline.Chunks[i];
            if (affected.Contains((held.Type.Value, held.ChunkIndex)))
            {
                continue;
            }

            records.Add(held.AsReused());
        }
    }

    static Dictionary<(ushort, int), List<ContentCandidateRow>> GroupByChunk(
        IReadOnlyList<ContentCandidateRow> rows,
        SortedSet<(ushort TypeId, int ChunkIndex)> affected)
    {
        var live = new Dictionary<(ushort, int), List<ContentCandidateRow>>();
        for (int i = 0; i < rows.Count; i++)
        {
            ContentCandidateRow row = rows[i];
            if (row.DefinitionId <= 0)
            {
                continue;
            }

            (ushort, int) address = (row.Registration.Type.Value, row.DefinitionId / row.Registration.ChunkSlots);
            if (!affected.Contains(address))
            {
                continue;
            }

            if (!live.TryGetValue(address, out List<ContentCandidateRow>? held))
            {
                held = [];
                live.Add(address, held);
            }

            held.Add(row);
        }

        return live;
    }

    static void Add(
        SortedSet<(ushort TypeId, int ChunkIndex)> affected,
        ContentTypeRegistry registry,
        ContentTypeId type,
        int definitionId)
    {
        if (definitionId <= 0)
        {
            return;
        }

        _ = affected.Add((type.Value, definitionId / RequireType(registry, type.Value).ChunkSlots));
    }

    static ContentTypeRegistration RequireType(ContentTypeRegistry registry, ushort typeId)
        => registry.TryGet(new ContentTypeId(typeId), out ContentTypeRegistration? registration)
            ? registration
            : throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Content type {typeId} is not registered, so the chunk range its rows fall into cannot be computed."),
                new ContentTypeId(typeId),
                0,
                ContentAuthoringException.UnknownTypeReason);
}
