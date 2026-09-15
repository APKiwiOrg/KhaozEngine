using System;
using System.Buffers;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// Pass 4 of the sweep, VISIBILITY AND CODEC (spec 5.3): field visibility against the SIDE a chunk is
/// encoded for, the codec round trip, the per-row size cap and the per-chunk size cap.
/// <para>
/// <b><c>KEC0027</c> is the check most likely to be skipped and it is the one that pays.</b> Every row is
/// encoded, decoded and encoded again, and the two byte sequences must match. It is the only thing that can
/// catch a codec whose encode and decode disagree BEFORE the bytes are hashed into a manifest an operator
/// then treats as an identity. It costs one pass over the candidate.
/// </para>
/// <para>
/// The encode path throws for a row that does not match its schema, because the encode path is ours and the
/// findings here are what a publish relies on to have caught it earlier. The validator must never throw for
/// a content reason, so a refused encode is CAUGHT and reported as <c>KEC0027</c>: a row the codec will not
/// write is a row whose round trip cannot hold.
/// </para>
/// </summary>
internal static class ContentVisibilityChecks
{
    internal static void Run(ContentValidationRun run)
    {
        CheckPerInstanceShape(run);
        CheckVisibility(run);

        foreach (ContentTypeRegistration registration in run.Registry.ByTypeId)
        {
            CheckType(run, registration);
        }
    }

    /// <summary>
    /// Walks one type's rows once: the encode, the round trip, the row cap, and the running per-chunk byte
    /// count the chunk cap is taken from.
    /// </summary>
    static void CheckType(ContentValidationRun run, ContentTypeRegistration registration)
    {
        IReadOnlyList<ContentRow> rows = run.Candidate.Rows(registration.Type);
        if (rows.Count == 0)
        {
            return;
        }

        var written = new ArrayBufferWriter<byte>();
        var again = new ArrayBufferWriter<byte>();
        var chunkBytes = new Dictionary<int, long>();

        foreach (ContentRow row in rows)
        {
            written.ResetWrittenCount();
            if (!TryEncode(run, registration, row, written, out ReadOnlySpan<byte> canonical))
            {
                continue;
            }

            if (canonical.Length > registration.MaxRowBytes)
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0026",
                    FormattableString.Invariant(
                        $"Row {row.Id} of type '{registration.TypeKey}' encodes to {canonical.Length} bytes, over the {registration.MaxRowBytes} byte cap the type declared at registration."));
            }

            CheckRoundTrip(run, registration, row, canonical, again);
            Accumulate(chunkBytes, registration, row.Id, canonical.Length);
        }

        CheckChunkSizes(run, registration, chunkBytes);
    }

    /// <summary>
    /// <c>KEC0014</c>: the CLIENT-side encoded bytes of a chunk still carrying a field whose visibility is
    /// <c>ServerOnly</c> (spec 6.7, contracts 11.3).
    /// <para>
    /// <b>It has no input in this milestone and so cannot fire.</b> Its trigger is an ENCODER defect, not an
    /// authoring one: the client chunk for a <c>Client</c> type is written with every <c>ServerOnly</c> field
    /// omitted, and the code fires when those bytes still carry one. No field-omitting client encoder exists
    /// yet, because <see cref="ContentChunkCodec"/> writes row bodies verbatim, so the check hangs off the
    /// client chunk encode and starts firing when that lands.
    /// </para>
    /// <para>
    /// <b>It is emphatically NOT a per-row check on the candidate.</b> A <c>Client</c> type MAY carry a
    /// per-field <c>ServerOnly</c> override, which is the case the two-chunk path of spec 6.7 exists for:
    /// nothing refuses it and nothing needs to. Reading the code as a refusal of an authored value is how
    /// <c>KEC0013</c> was written, and spec 5.2 WITHDREW that code for exactly this mistake. It also made a
    /// <c>Client</c> type with a REQUIRED <c>ServerOnly</c> field unpublishable both ways, absent drawing
    /// <c>KEC0005</c> and present drawing <c>KEC0014</c>. A type whose registration default is
    /// <c>ServerOnly</c> is separately omitted from the client manifest whole, so its own fields never reach
    /// a client either.
    /// </para>
    /// <para>
    /// Left as the named hook rather than as a code nobody can find, because the code is issued and a reader
    /// looking for <c>KEC0014</c> has to land somewhere that says why it is quiet.
    /// </para>
    /// </summary>
    static void CheckVisibility(ContentValidationRun run)
    {
        _ = run;
    }

    /// <summary>
    /// <c>KEC0022</c>: a definition declaring durability or sockets is stackable. Stacking is byte equality
    /// over payloads and both of those are PER INSTANCE, so the two are contradictory rather than merely
    /// unusual.
    /// </summary>
    static void CheckPerInstanceShape(ContentValidationRun run)
    {
        if (!run.TryGetEngineType(EngineContentTypes.ItemTypeKey, out ContentTypeRegistration? item))
        {
            return;
        }

        int stackableIndex = ContentValidationRun.FieldIndex(item.Schema, ItemContentType.StackableField);
        int durabilityIndex = ContentValidationRun.FieldIndex(item.Schema, ItemContentType.DurabilityMaxField);
        int socketIndex = ContentValidationRun.FieldIndex(item.Schema, ItemContentType.SocketMaxField);

        foreach (ContentRow row in run.Candidate.Rows(item.Type))
        {
            if (ContentValidationRun.Value(row, stackableIndex, ContentFieldKind.Bool).Number == 0)
            {
                continue;
            }

            long durability = ContentValidationRun.Value(row, durabilityIndex, ContentFieldKind.Int).Number;
            long sockets = ContentValidationRun.Value(row, socketIndex, ContentFieldKind.Int).Number;
            if (durability <= 0 && sockets <= 0)
            {
                continue;
            }

            run.Add(
                item.Type,
                row.Id,
                "KEC0022",
                FormattableString.Invariant(
                    $"Item '{row.Key}' is stackable and declares durability {durability} and {sockets} sockets. Both are per instance and a stack is byte equality over payloads, so the two cannot hold at once."));
        }
    }

    /// <summary>
    /// Encodes one row, reporting a REFUSED encode as <c>KEC0027</c>. The row is then skipped for the size
    /// and round-trip checks, because there are no canonical bytes to measure.
    /// </summary>
    static bool TryEncode(
        ContentValidationRun run,
        ContentTypeRegistration registration,
        ContentRow row,
        ArrayBufferWriter<byte> writer,
        out ReadOnlySpan<byte> canonical)
    {
        try
        {
            registration.Codec.Encode(row, writer);
        }
        catch (ArgumentException ex)
        {
            run.Add(
                registration.Type,
                row.Id,
                "KEC0027",
                FormattableString.Invariant(
                    $"Row {row.Id} of type '{registration.TypeKey}' is a row its own codec refuses to write, '{ex.Message}'. A row that cannot be encoded has no round trip to be byte identical across."));
            canonical = default;
            return false;
        }

        canonical = writer.WrittenSpan;
        return true;
    }

    /// <summary>
    /// <c>KEC0027</c>: encode, decode, encode again, and compare the two byte sequences. The decoded row
    /// carries id 0 and a live retired bit because a row BODY holds neither, so only the key and the fields
    /// are compared and that is exactly the part the codec owns.
    /// </summary>
    static void CheckRoundTrip(
        ContentValidationRun run,
        ContentTypeRegistration registration,
        ContentRow row,
        ReadOnlySpan<byte> canonical,
        ArrayBufferWriter<byte> writer)
    {
        if (!registration.Codec.TryDecode(canonical, out ContentRow? decoded, out string? reason))
        {
            run.Add(
                registration.Type,
                row.Id,
                "KEC0027",
                FormattableString.Invariant(
                    $"Row {row.Id} of type '{registration.TypeKey}' encodes to {canonical.Length} bytes its own decoder refuses, '{reason}'. An encoder and a decoder that disagree write a pack nothing can read back."));
            return;
        }

        writer.ResetWrittenCount();
        if (!TryEncode(run, registration, decoded.WithIdentity(row.Id, row.IsRetired), writer, out ReadOnlySpan<byte> second))
        {
            return;
        }

        if (canonical.SequenceEqual(second))
        {
            return;
        }

        run.Add(
            registration.Type,
            row.Id,
            "KEC0027",
            FormattableString.Invariant(
                $"Row {row.Id} of type '{registration.TypeKey}' is not byte identical across its codec's round trip: {canonical.Length} bytes out, {second.Length} bytes back. The chunk hash is taken over these bytes, so a round trip that drifts makes an identity an operator cannot trust."));
    }

    /// <summary>
    /// Adds one row's cost to its chunk: the row table entry of a varint id, a flags byte and a varint
    /// length, plus the body itself. A chunk boundary is <c>floor(id / chunkSlots)</c>, and an id pass 1
    /// already refused is left out rather than counted into a chunk that does not exist.
    /// </summary>
    static void Accumulate(Dictionary<int, long> chunkBytes, ContentTypeRegistration registration, int id, int length)
    {
        if (id <= 0)
        {
            return;
        }

        int chunk = id / registration.ChunkSlots;
        long entry = ContentVarint.Size((uint)id) + 1 + ContentVarint.Size((uint)length) + length;
        chunkBytes[chunk] = chunkBytes.TryGetValue(chunk, out long running) ? running + entry : entry;
    }

    /// <summary>
    /// <c>KEC0038</c>: a chunk's canonical UNCOMPRESSED bytes over the ceiling. The ceiling is checked from
    /// a chunk header alone at read time, before a body byte is read, so a chunk a publish writes past it is
    /// a chunk no reader will ever load.
    /// </summary>
    static void CheckChunkSizes(
        ContentValidationRun run,
        ContentTypeRegistration registration,
        Dictionary<int, long> chunkBytes)
    {
        foreach (KeyValuePair<int, long> chunk in chunkBytes)
        {
            long canonical = chunk.Value + ContentPackFormat.ChunkHeaderBytes;
            if (canonical <= ContentPackFormat.MaxChunkUncompressedBytes)
            {
                continue;
            }

            run.Add(
                registration.Type,
                0,
                "KEC0038",
                FormattableString.Invariant(
                    $"Chunk {chunk.Key} of type '{registration.TypeKey}' is {canonical} canonical uncompressed bytes, over the {ContentPackFormat.MaxChunkUncompressedBytes} byte ceiling. A reader refuses that from the header alone, so the chunk would never load."));
        }
    }
}
