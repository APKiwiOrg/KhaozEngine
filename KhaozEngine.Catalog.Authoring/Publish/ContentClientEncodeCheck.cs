using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// <c>KEC0014</c>, and the ONE thing it fires on: the CLIENT-side encoded bytes of a chunk still carrying a
/// field the schema marks <c>ServerOnly</c> (spec 6.7, contracts 11.3).
/// <para>
/// <b>That is an ENCODER defect and never an authoring one.</b> A <c>Client</c> type MAY carry a per-field
/// <c>ServerOnly</c> override, which is the case the two-chunk path exists for: nothing refuses it and
/// nothing needs to, because the client chunk omits those fields at encode time. Reading the code as a
/// refusal of an authored value is how <c>KEC0013</c> was written, and spec 5.2 withdrew that code for
/// exactly this mistake, because it also made a <c>Client</c> type with a REQUIRED <c>ServerOnly</c> field
/// unpublishable both ways, absent drawing <c>KEC0005</c> and present drawing <c>KEC0014</c>.
/// </para>
/// <para>
/// <b>It reads the bytes back rather than trusting the encoder that wrote them.</b> The chunk is decoded the
/// way a client would decode it and every row is walked through its type's schema, so an encoder that
/// skipped the mask is caught by the thing it produced rather than by an assertion about what it intended.
/// A silent STRIP here would be worse than the refusal, because then a field's absence on the client would
/// be indistinguishable from an authoring mistake.
/// </para>
/// </summary>
static class ContentClientEncodeCheck
{
    /// <summary>The stable code, spec 5.2's.</summary>
    public const string Code = "KEC0014";

    /// <summary>
    /// Walks one client-side chunk and reports every row that still carries a <c>ServerOnly</c> value.
    /// </summary>
    /// <param name="registry">The registry the chunk's declared range is checked against.</param>
    /// <param name="type">The registration whose schema says which fields are server only.</param>
    /// <param name="chunk">The encoded client chunk, read through its canonical bytes.</param>
    /// <param name="findings">Where the findings land.</param>
    public static void Run(
        ContentTypeRegistry registry,
        ContentTypeRegistration type,
        EncodedContentChunk chunk,
        ICollection<ContentFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(findings);

        if (!ContentChunkCodec.TryDecode(chunk.Canonical.Span, registry, out ContentChunk? decoded, out string? reason))
        {
            findings.Add(new ContentFinding(
                type.Type,
                0,
                Code,
                FormattableString.Invariant(
                    $"The client chunk {chunk.ChunkIndex} of type '{type.TypeKey}' is bytes its own reader refuses, '{reason}', so whether it carries a ServerOnly field cannot be answered. A chunk no client can decode is not a chunk to publish.")));
            return;
        }

        IReadOnlyList<ContentFieldEntry> schema = type.Schema.Fields;
        for (int index = 0; index < decoded.RowCount; index++)
        {
            if (!decoded.TryDecodeRowAt(index, type.Codec, out ContentRow? row, out reason))
            {
                findings.Add(new ContentFinding(
                    type.Type,
                    decoded.DefinitionIdAt(index),
                    Code,
                    FormattableString.Invariant(
                        $"Row {decoded.DefinitionIdAt(index)} of the client chunk {chunk.ChunkIndex} of type '{type.TypeKey}' is bytes its own decoder refuses, '{reason}'.")));
                continue;
            }

            CheckRow(type, schema, chunk.ChunkIndex, row, findings);
        }
    }

    /// <summary>
    /// One row's <c>ServerOnly</c> fields as the CLIENT reads them. The field has to be absent or carry the
    /// ZERO form of its kind, which is what the absence convention writes, because a required field's zero
    /// form comes back present rather than absent and both are the same one byte on the wire.
    /// </summary>
    static void CheckRow(
        ContentTypeRegistration type,
        IReadOnlyList<ContentFieldEntry> schema,
        int chunkIndex,
        ContentRow row,
        ICollection<ContentFinding> findings)
    {
        int fields = Math.Min(schema.Count, row.Fields.Count);
        for (int i = 0; i < fields; i++)
        {
            ContentFieldEntry entry = schema[i];
            if (entry.Visibility != ContentVisibility.ServerOnly || IsOmitted(row.Fields[i]))
            {
                continue;
            }

            findings.Add(new ContentFinding(
                type.Type,
                row.Id,
                Code,
                FormattableString.Invariant(
                    $"Row {row.Id} of the client chunk {chunkIndex} of type '{type.TypeKey}' still carries field '{entry.Name}', which the schema marks ServerOnly. The client chunk is encoded with those fields omitted, so bytes that keep one are the codec failing to honour the schema rather than an authoring mistake.")));
        }
    }

    /// <summary>
    /// Omitted means the field went out as the ZERO FORM of its kind, which is what
    /// <c>ContentSideRowEncoder</c> writes in place of a <c>ServerOnly</c> value, and which the decoder hands
    /// back absent. The predicate is <see cref="ContentFieldValue.IsZeroForm"/> rather than a copy of it, so
    /// this check and the tail rule can never come to different conclusions about the same bytes.
    /// </summary>
    static bool IsOmitted(in ContentFieldValue value) => value.IsZeroForm;
}
