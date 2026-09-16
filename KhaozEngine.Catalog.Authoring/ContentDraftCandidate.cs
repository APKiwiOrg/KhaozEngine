using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The DRY RUN of spec 10.6: the base version with the open draft's edits applied, swept by the one
/// validator, with nothing allocated and nothing written.
/// <para>
/// <b>It exists because two read-only actions need the candidate a publish builds at step 2</b>, and the
/// seam offers no way to reach it. <c>catalog-validate</c> is the sweep an operator runs before publishing
/// and <c>catalog-diff</c> answers "what would publishing this change", and both are about a row set that
/// has no version number yet. Running a publish to find out would defeat the purpose of asking.
/// </para>
/// <para>
/// <b>Every id a draft's new rows carry here is PROVISIONAL.</b> Ids are allocated at publish through the
/// reserve-then-issue allocator (spec 6.3), so a dry run cannot know them without reserving, and reserving
/// is a durable write. The provisional numbering opens above every id the candidate already carries, which
/// is enough for the sweep to ask its questions: a reference resolves, two rows do not share an id, and no
/// id is 0. The family block checks take no part, because they have no input at all in phase 1.
/// A publish therefore issues DIFFERENT numbers, and a caller showing these to an operator says so.
/// </para>
/// </summary>
public sealed class ContentDraftCandidate
{
    ContentDraftCandidate(
        int baseVersion,
        int candidateVersion,
        IReadOnlyList<ContentRowRevision> rows,
        IReadOnlyList<RemapRule> rules,
        IReadOnlyList<RemapRule> appendedRules,
        ContentSnapshot snapshot,
        ContentValidationReport report)
    {
        BaseVersion = baseVersion;
        CandidateVersion = candidateVersion;
        Rows = rows;
        Rules = rules;
        AppendedRules = appendedRules;
        Snapshot = snapshot;
        Report = report;
    }

    /// <summary>The published version the draft stands on, 0 on a store that has published none.</summary>
    public int BaseVersion { get; }

    /// <summary>The number publishing this draft WOULD assign, which is the base plus one.</summary>
    public int CandidateVersion { get; }

    /// <summary>Every row live at the candidate, the untouched ones included, with provisional ids on the new.</summary>
    public IReadOnlyList<ContentRowRevision> Rows { get; }

    /// <summary>The full rule list as it would stand, the base version's plus this draft's.</summary>
    public IReadOnlyList<RemapRule> Rules { get; }

    /// <summary>The rules this draft would append, which is what a retire and a fork each add one of.</summary>
    public IReadOnlyList<RemapRule> AppendedRules { get; }

    /// <summary>The candidate as a snapshot, which is what the validator swept.</summary>
    public ContentSnapshot Snapshot { get; }

    /// <summary>Every finding the sweep accumulated, in sweep order, and whether the candidate is publishable.</summary>
    public ContentValidationReport Report { get; }

    /// <summary>
    /// Builds the candidate and sweeps it.
    /// </summary>
    /// <param name="baseline">The base version, which <see cref="IContentAuthoringStore.ReadPublishBaselineAsync"/> reads.</param>
    /// <param name="changes">The open draft's change set, empty for a store with no draft open.</param>
    /// <param name="registry">The registry the edits' types are declared in. Per instance, never ambient.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ContentAuthoringException">An edit names a type, a row or a field the registry or the base version does not carry.</exception>
    public static ContentDraftCandidate Build(
        ContentPublishBaseline baseline,
        ContentChangeSet changes,
        ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(registry);

        int version = baseline.VersionNumber + 1;

        // The fork preconditions come FIRST, exactly as they do in a publish, because a fork of a row that is
        // not there cannot be applied at all and the refusal has to come from the edit rather than from the
        // middle of the walk that applies it.
        IReadOnlyList<ContentFinding> forkFindings = ContentForkChecks.Check(baseline, changes, registry);
        if (forkFindings.Count > 0)
        {
            return new ContentDraftCandidate(
                baseline.VersionNumber,
                version,
                baseline.Rows,
                baseline.Rules,
                [],
                Materialize(registry, baseline.Rows, baseline.Rules, baseline.VersionNumber),
                new ContentValidationReport(false, forkFindings));
        }

        (List<ContentCandidateRow> rows, List<ContentPendingRule> pending) =
            ContentCandidateBuilder.Build(baseline, changes, registry, version);
        AssignProvisionalIds(rows);

        IReadOnlyList<RemapRule> appended = Append(pending, baseline, version);
        IReadOnlyList<RemapRule> rules = Concat(baseline.Rules, appended);

        var builder = new ContentSnapshotBuilder(registry);
        builder.WithIdentity(version, string.Empty).WithRules(rules);
        var revisions = new List<ContentRowRevision>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            ContentCandidateRow row = rows[i];
            ContentRow materialized = row.ToRow();
            builder.AddRow(materialized);
            revisions.Add(new ContentRowRevision(materialized, row.ValidFromVersion, null, row.FamilyId));
        }

        // previous is the base version, and this is the one caller other than a publish that has one. A first
        // publish into an empty store passes null and takes KEC0000 like any other null run.
        ContentSnapshot? previous = baseline.IsEmpty
            ? null
            : Materialize(registry, baseline.Rows, rules, baseline.VersionNumber);

        ContentSnapshot candidate = builder.Build();
        return new ContentDraftCandidate(
            baseline.VersionNumber,
            version,
            revisions,
            rules,
            appended,
            candidate,
            ContentValidator.Validate(candidate, previous, rules, registry));
    }

    /// <summary>
    /// Numbers every row that would be allocated one, per type, starting ABOVE every id the candidate
    /// already carries. It is not the allocator's answer and does not pretend to be: the allocator opens at
    /// the type's durable issued mark, which a dry run cannot read without a write, and a retired row keeps
    /// its id forever so the mark is at or above this number rather than below it.
    /// </summary>
    static void AssignProvisionalIds(List<ContentCandidateRow> rows)
    {
        var next = new Dictionary<ushort, int>();
        for (int i = 0; i < rows.Count; i++)
        {
            ContentCandidateRow row = rows[i];
            if (row.DefinitionId <= 0)
            {
                continue;
            }

            ushort type = row.Registration.Type.Value;
            next[type] = next.TryGetValue(type, out int held)
                ? Math.Max(held, row.DefinitionId + 1)
                : row.DefinitionId + 1;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            ContentCandidateRow row = rows[i];
            if (!row.IsNewDefinition || !row.NeedsId)
            {
                continue;
            }

            ushort type = row.Registration.Type.Value;
            int id = next.TryGetValue(type, out int held) ? held : 1;
            row.DefinitionId = id;
            next[type] = id + 1;
        }
    }

    /// <summary>
    /// The rules this draft would append, numbered on from the base version's highest, which is what keeps
    /// the sequence contiguous from 1 across the whole list.
    /// </summary>
    static IReadOnlyList<RemapRule> Append(
        List<ContentPendingRule> pending,
        ContentPublishBaseline baseline,
        int version)
    {
        if (pending.Count == 0)
        {
            return [];
        }

        var appended = new RemapRule[pending.Count];
        for (int i = 0; i < pending.Count; i++)
        {
            ContentPendingRule rule = pending[i];
            appended[i] = new RemapRule(
                baseline.Rules.Count + i + 1,
                version,
                rule.Type,
                rule.Kind,
                rule.From.DefinitionId,
                rule.To?.DefinitionId ?? 0,
                rule.Payload);
        }

        return appended;
    }

    static IReadOnlyList<RemapRule> Concat(IReadOnlyList<RemapRule> first, IReadOnlyList<RemapRule> second)
    {
        if (second.Count == 0)
        {
            return first;
        }

        var all = new List<RemapRule>(first.Count + second.Count);
        all.AddRange(first);
        all.AddRange(second);
        return all;
    }

    static ContentSnapshot Materialize(
        ContentTypeRegistry registry,
        IReadOnlyList<ContentRowRevision> rows,
        IReadOnlyList<RemapRule> rules,
        int version)
    {
        var builder = new ContentSnapshotBuilder(registry);
        builder.WithIdentity(version, string.Empty).WithRules(rules);
        for (int i = 0; i < rows.Count; i++)
        {
            builder.AddRow(rows[i].Row);
        }

        return builder.Build();
    }
}
