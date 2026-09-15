using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The PUBLISHED half of the in-memory store: the baseline a publish is prepared against, the one commit
/// transaction, the family read, the whole publish, the snapshot load and the rollback draft.
/// <para>
/// It is a separate file from the draft and allocator half because the two answer different questions. The
/// other half is what a console does between publishes, and this one is what a publish does, which is also
/// the split every provider will carry: a draft edit is one statement and a publish is one transaction.
/// </para>
/// </summary>
public sealed partial class InMemoryContentAuthoringStore
{
    readonly List<RemapRule> _rules = [];
    readonly Dictionary<int, PublishedVersion> _published = [];

    /// <summary>
    /// The pack store a publish writes to, or null on a store that can hold a draft and allocate ids and
    /// cannot publish. A publish writes files before it writes rows, so the target is not optional for it.
    /// </summary>
    public IPackStore? PackStore { get; private set; }

    /// <summary>The full ordered remap rule list as it stands, which is what a published version carries.</summary>
    public IReadOnlyList<RemapRule> Rules
    {
        get
        {
            lock (_gate)
            {
                return _rules.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var families = new List<ContentFamily>();
            foreach (FamilyRecord held in _families.Values)
            {
                if (type.Value == 0 || held.Type == type)
                {
                    families.Add(held.ToFamily());
                }
            }

            families.Sort(static (left, right) => left.FamilyId.CompareTo(right.FamilyId));
            return Task.FromResult<IReadOnlyList<ContentFamily>>(families);
        }
    }

    /// <inheritdoc />
    public Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(ReadBaseline());
        }
    }

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);
        throw NotYetPublishing(nameof(CommitPublishAsync));
    }

    /// <summary>The baseline as it stands, which the caller already holds the gate for.</summary>
    ContentPublishBaseline ReadBaseline()
    {
        ContentVersionRecord? record = FindVersion(_activeVersion);
        var rows = new List<ContentRowRevision>();
        for (int i = 0; i < _rows.Count; i++)
        {
            if (IsLiveAt(_rows[i], _activeVersion))
            {
                rows.Add(_rows[i]);
            }
        }

        PublishedVersion? held = _published.TryGetValue(_activeVersion, out PublishedVersion? version)
            ? version
            : null;

        return new ContentPublishBaseline(
            _activeVersion,
            rows,
            _rules,
            held?.Chunks ?? [],
            held?.Languages ?? [],
            record?.MinimumServerBuild ?? 0,
            record?.MinimumClientBuild ?? 0);
    }

    /// <summary>
    /// One published version's pack-side facts, which no other table holds: the chunk rows it carries at
    /// every side, and the text chunks it names. Both are carried FORWARD by the next publish, so the store
    /// keeps them per version rather than only for the active one.
    /// </summary>
    /// <param name="Chunks">Every chunk row of the version, in the reused form a carry forward reads.</param>
    /// <param name="Languages">The version's text chunks, one per language.</param>
    sealed record PublishedVersion(
        IReadOnlyList<ContentChunkRecord> Chunks,
        IReadOnlyList<ManifestLanguageEntry> Languages);
}
