using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Everything a text-bearing publish commits, in one object: the ordinary row, rule and version plan, the
/// frozen snapshot it was built from, the merged text candidate with its temporal closes and inserts, and
/// every output language with its chunk. <see cref="IContentTextAuthoringStore.CommitTextPublishAsync"/>
/// commits all of it together, and nothing commits part of it.
/// <para>
/// <b>The constructor proves the parts agree.</b> The row plan follows the snapshot's base and froze its
/// exact rows. Every output language has exactly one chunk, in ordinal wire-tag order, and both manifests
/// name exactly those chunks. Every baseline language survives with its historical spelling and every
/// pending introduction appears with its canonical one. The candidate's values are the baseline's minus its
/// closes plus its inserts, and they apply every frozen intent: each Set's exact value is live at its row's
/// final id and each Remove leaves nothing live there. What it cannot prove is that the snapshot still
/// matches the store, which is why the store confirms that inside its own commit.
/// </para>
/// <para>
/// <see cref="RowPlan"/> is a MARKED copy of the row plan handed in, carrying the frozen text state, so a
/// wrapper that extracts it and calls a row-only commit is refused rather than publishing rows without text.
/// </para>
/// </summary>
public sealed class ContentTextPublishPlan
{
    /// <summary>Builds one plan.</summary>
    /// <param name="rowPlan">The valid row plan prepared from the snapshot's frozen rows.</param>
    /// <param name="snapshot">The frozen snapshot the plan was built from.</param>
    /// <param name="candidate">The merged text candidate.</param>
    /// <param name="chunks">One chunk per declared language, in the candidate's declaration order.</param>
    /// <exception cref="ArgumentNullException">An argument or an entry is null.</exception>
    /// <exception cref="ArgumentException">The parts disagree, as the class remarks describe.</exception>
    public ContentTextPublishPlan(
        ContentPublishPlan rowPlan,
        ContentTextPublishSnapshot snapshot,
        ContentTextCandidate candidate,
        IReadOnlyList<ContentTextChunkRecord> chunks)
    {
        ArgumentNullException.ThrowIfNull(rowPlan);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(chunks);

        RequireRowPlan(rowPlan, snapshot);
        Languages = MapLanguages(rowPlan, candidate, chunks, out ContentTextChunkRecord[] owned);
        RequireDeclarations(snapshot, candidate);
        RequireTemporal(rowPlan, snapshot, candidate);
        RequireFrozenIntents(rowPlan, snapshot.TextState, candidate);

        RowPlan = rowPlan.WithFrozenText(snapshot.TextState);
        Snapshot = snapshot;
        Candidate = candidate;
        Chunks = owned;
    }

    /// <summary>The ordinary row plan, marked as the row half of this plan.</summary>
    public ContentPublishPlan RowPlan { get; }

    /// <summary>The frozen snapshot this plan was built from.</summary>
    public ContentTextPublishSnapshot Snapshot { get; }

    /// <summary>The merged text candidate.</summary>
    public ContentTextCandidate Candidate { get; }

    /// <summary>One chunk per output language, in ordinal wire-tag order.</summary>
    public IReadOnlyList<ContentTextChunkRecord> Chunks { get; }

    /// <summary>Every output language with its identity, wire spelling and chunk hash, in manifest order.</summary>
    public IReadOnlyList<ContentTextLanguage> Languages { get; }

    /// <summary>The store the snapshot was frozen in.</summary>
    public string StoreEpoch => Snapshot.StoreEpoch;

    /// <summary>The version this plan commits.</summary>
    public int VersionNumber => RowPlan.VersionNumber;

    /// <summary>The base version the plan stands on.</summary>
    public int BaseVersion => RowPlan.BaseVersion;

    /// <summary>The frozen text state the commit consumes from the draft.</summary>
    public ContentDraftTextState FrozenText => Snapshot.TextState;

    /// <summary>The baseline revisions the commit closes.</summary>
    public IReadOnlyList<ContentTextRevision> TextCloses => Candidate.Closes;

    /// <summary>The revisions the commit inserts.</summary>
    public IReadOnlyList<ContentTextRevision> TextInserts => Candidate.Inserts;

    static void RequireRowPlan(ContentPublishPlan rowPlan, ContentTextPublishSnapshot snapshot)
    {
        if (!rowPlan.IsValid || rowPlan.FrozenTextState is not null)
        {
            throw new ArgumentException(
                "A text plan wraps one valid, unmarked row plan.", nameof(rowPlan));
        }

        if (rowPlan.BaseVersion != snapshot.BaseVersion || rowPlan.VersionNumber != snapshot.BaseVersion + 1)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The row plan publishes version {rowPlan.VersionNumber} on base {rowPlan.BaseVersion}, and the snapshot is frozen for base {snapshot.BaseVersion}."),
                nameof(rowPlan));
        }

        if (!ContentTextCompatibility.SameEdits(rowPlan.FrozenEdits, snapshot.Draft.Changes.Edits))
        {
            throw new ArgumentException(
                "The row plan froze other rows than the snapshot's draft holds.", nameof(rowPlan));
        }
    }

    static ContentTextLanguage[] MapLanguages(
        ContentPublishPlan rowPlan,
        ContentTextCandidate candidate,
        IReadOnlyList<ContentTextChunkRecord> chunks,
        out ContentTextChunkRecord[] owned)
    {
        IReadOnlyList<ContentTextLanguageDeclaration> declarations = candidate.Declarations;
        if (chunks.Count != declarations.Count || rowPlan.Languages.Count != declarations.Count)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The candidate declares {declarations.Count} language(s), with {chunks.Count} chunk(s) and {rowPlan.Languages.Count} manifest entr(ies)."),
                nameof(chunks));
        }

        owned = new ContentTextChunkRecord[chunks.Count];
        var languages = new ContentTextLanguage[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            ContentTextChunkRecord chunk = chunks[i] ?? throw new ArgumentNullException(
                nameof(chunks), FormattableString.Invariant($"Chunk {i} is null."));
            ContentTextLanguageDeclaration declaration = declarations[i];
            ManifestLanguageEntry entry = rowPlan.Languages[i];
            if (!string.Equals(chunk.WireTag, declaration.WireTag, StringComparison.Ordinal)
                || !string.Equals(entry.Tag, chunk.WireTag, StringComparison.Ordinal)
                || !string.Equals(entry.TextHash, chunk.Hash, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Language {i} is declared as '{declaration.WireTag}', chunked as '{chunk.WireTag}' and named by the manifests as '{entry.Tag}'."),
                    nameof(chunks));
            }

            if (i > 0 && string.CompareOrdinal(declarations[i - 1].WireTag, declaration.WireTag) >= 0)
            {
                throw new ArgumentException(
                    "Output languages are in strictly ascending ordinal wire-tag order.", nameof(chunks));
            }

            owned[i] = chunk;
            languages[i] = new ContentTextLanguage(declaration.Language, declaration.WireTag, chunk.Hash);
        }

        return languages;
    }

    static void RequireDeclarations(ContentTextPublishSnapshot snapshot, ContentTextCandidate candidate)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ContentTextLanguage held in snapshot.BaselineText.Languages)
        {
            expected[held.Language] = held.WireTag;
        }

        foreach (ContentTextLanguageDeclaration introduced in snapshot.TextState.Introductions)
        {
            expected.TryAdd(introduced.Language, introduced.WireTag);
        }

        IReadOnlyList<ContentTextLanguageDeclaration> output = candidate.Declarations;
        bool agrees = output.Count == expected.Count;
        for (int i = 0; agrees && i < output.Count; i++)
        {
            agrees = expected.TryGetValue(output[i].Language, out string? spelling)
                && string.Equals(spelling, output[i].WireTag, StringComparison.Ordinal);
        }

        if (!agrees)
        {
            throw new ArgumentException(
                "The output languages are not exactly the baseline's, in their recorded spelling, plus the draft's introductions.",
                nameof(candidate));
        }
    }

    static void RequireTemporal(
        ContentPublishPlan rowPlan,
        ContentTextPublishSnapshot snapshot,
        ContentTextCandidate candidate)
    {
        var rows = new HashSet<(ushort, int)>();
        foreach (ContentRowRevision live in rowPlan.LiveRows)
        {
            rows.Add((live.Row.Type.Value, live.Row.Id));
        }

        var visible = new List<ContentTextRevision>(snapshot.BaselineText.Revisions);
        foreach (ContentTextRevision close in candidate.Closes)
        {
            if (!visible.Remove(close))
            {
                throw new ArgumentException(
                    "A text close names a revision the baseline does not hold live.", nameof(candidate));
            }
        }

        foreach (ContentTextRevision insert in candidate.Inserts)
        {
            if (insert.ValidFromVersion != rowPlan.VersionNumber
                || !rows.Contains((insert.Type.Value, insert.DefinitionId))
                || visible.Exists(held => held.IsSameString(insert)))
            {
                throw new ArgumentException(
                    "A text insert is not valid from the new version, names no live row, or shadows a value it does not close.",
                    nameof(candidate));
            }

            visible.Add(insert);
        }

        bool agrees = visible.Count == candidate.Values.Count;
        foreach (ContentTextRevision value in candidate.Values)
        {
            agrees = agrees && visible.Contains(value);
        }

        if (!agrees)
        {
            throw new ArgumentException(
                "The candidate's values are not the baseline's minus its closes plus its inserts.", nameof(candidate));
        }
    }

    /// <summary>
    /// Proves the candidate applies every frozen text intent. Each target's key is bound to its final
    /// definition id through the row plan's live rows, which name a pending add by the id the row plan
    /// allocated and a fork copy by its legacy key, so no allocator branch is assumed. A fork copy's baseline
    /// values sit on the copy's own id, so an explicit edit on the original key is checked on the original
    /// alone. A Set must leave exactly its value live and a Remove must leave none.
    /// </summary>
    static void RequireFrozenIntents(
        ContentPublishPlan rowPlan,
        ContentDraftTextState frozen,
        ContentTextCandidate candidate)
    {
        var ids = new Dictionary<(ushort Type, ContentKey Key), int>();
        var ambiguous = new HashSet<(ushort Type, ContentKey Key)>();
        foreach (ContentRowRevision live in rowPlan.LiveRows)
        {
            (ushort, ContentKey) row = (live.Row.Type.Value, live.Row.Key);
            if (!ids.TryAdd(row, live.Row.Id) && ids[row] != live.Row.Id)
            {
                ambiguous.Add(row);
            }
        }

        var values = new Dictionary<(ushort Type, int Id, string Field, string Language), string>();
        foreach (ContentTextRevision value in candidate.Values)
        {
            values.Add((value.Type.Value, value.DefinitionId, value.FieldName, value.Language), value.Value);
        }

        foreach (ContentTextEdit edit in frozen.Edits)
        {
            ContentTextTarget target = edit.Target;
            (ushort, ContentKey) row = (target.Type.Value, target.Key);
            if (!ids.TryGetValue(row, out int id) || ambiguous.Contains(row))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"A frozen text intent names type {target.Type.Value} row '{target.Key}', which the row plan holds no single live row of."),
                    nameof(rowPlan));
            }

            bool live = values.TryGetValue((target.Type.Value, id, target.FieldName, target.Language), out string? held);
            bool applied = edit.Operation == ContentTextEditOperation.Set
                ? live && string.Equals(held, edit.Value, StringComparison.Ordinal)
                : !live;
            if (!applied)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"The candidate does not apply the frozen {edit.Operation} of type {target.Type.Value} row {id} field '{target.FieldName}' language '{target.Language}'."),
                    nameof(candidate));
            }
        }
    }
}
