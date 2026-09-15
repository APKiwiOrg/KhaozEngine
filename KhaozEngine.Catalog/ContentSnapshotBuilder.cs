using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// The ONE way a <see cref="ContentSnapshot"/> is made, so a publish, a boot and a test all build one the
/// same way. It takes a registry, accepts rows per type, accepts the rule list and the version identity, and
/// hands back an immutable snapshot whose rows are ordered by id whatever order they went in.
/// <para>
/// Spec 5.4 requires that a test builds a snapshot "with no store, no file and no registry beyond the one
/// they construct" without naming a construction path. This is that path, and it is the only one.
/// </para>
/// <para>
/// <b>It refuses a programming error and never a content defect.</b> A null row, a null rule list and a row
/// of a type the registry never registered all throw, because none of them can be reported as a finding. A
/// duplicate id, a duplicate key, an id of 0 and a malformed key all go in untouched, because every one of
/// them IS a finding and the validator sweeps the snapshot to report it.
/// </para>
/// <para>
/// A row may arrive WITH the encoded body a chunk already holds, which is what the pack load path hands in
/// and what the typed <see cref="ItemRow"/> view reads. A row added without one is read generically, through
/// <see cref="ContentSnapshot.TryGetRow"/>, and nothing re-encodes it: encoding a row the snapshot was handed
/// is the validator's job (<c>KEC0026</c> and <c>KEC0027</c>), and a builder that did it eagerly could not
/// hold the rows those findings exist to report.
/// </para>
/// </summary>
public sealed class ContentSnapshotBuilder
{
    readonly SortedDictionary<ushort, List<Entry>> _byType = [];
    RemapRule[] _rules = [];
    ContentVersionIdentity _identity = new(0, string.Empty);

    /// <summary>Builds over a registry, which is what says a type exists at all.</summary>
    /// <param name="registry">The registry the rows' types are checked against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    public ContentSnapshotBuilder(ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
    }

    /// <summary>The registry this snapshot's types come from.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>
    /// Sets the version identity, contracts 7.1. A builder that is never told one produces a candidate
    /// carrying number 0 and an empty hash, which is what a snapshot that has not been published yet is.
    /// </summary>
    /// <param name="versionNumber">The version number, monotonic from 1.</param>
    /// <param name="manifestHash">The manifest digest, lower hex, or empty on a candidate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifestHash"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="versionNumber"/> is negative.</exception>
    public ContentSnapshotBuilder WithIdentity(int versionNumber, string manifestHash)
    {
        ArgumentNullException.ThrowIfNull(manifestHash);
        ArgumentOutOfRangeException.ThrowIfNegative(versionNumber);
        _identity = new ContentVersionIdentity(versionNumber, manifestHash);
        return this;
    }

    /// <summary>
    /// Adds one row, read generically. Its own <see cref="ContentRow.Type"/> is the type it lands under.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    /// <exception cref="ContentRegistrationException">The row's type is not registered.</exception>
    public ContentSnapshotBuilder AddRow(ContentRow row) => AddRow(row, default);

    /// <summary>
    /// Adds one row together with the encoded body a chunk carries for it, which is what the typed
    /// <see cref="ItemRow"/> view reads. The bytes are COPIED into the snapshot's per-type blob at
    /// <see cref="Build"/>, so the caller's buffer is its own afterwards.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    /// <exception cref="ContentRegistrationException">The row's type is not registered.</exception>
    public ContentSnapshotBuilder AddRow(ContentRow row, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!Registry.TryGet(row.Type, out _))
        {
            throw new ContentRegistrationException(FormattableString.Invariant(
                $"Content type {row.Type.Value} is not registered, so row {row.Id} cannot go into a snapshot. Registration runs once at process start, before the first pack loads."));
        }

        if (!_byType.TryGetValue(row.Type.Value, out List<Entry>? rows))
        {
            rows = [];
            _byType.Add(row.Type.Value, rows);
        }

        rows.Add(new Entry(row, body, rows.Count));
        return this;
    }

    /// <summary>
    /// Sets the remap rule list, contracts 8.1, in sequence order. The list is COPIED, and it is a plain
    /// list rather than a <see cref="RemapRuleSet"/> because a set that is not contiguous or not ascending is
    /// <c>KEC0018</c>, and the validator has to be able to hold it to report it.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> or one of its entries is null.</exception>
    public ContentSnapshotBuilder WithRules(IReadOnlyList<RemapRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var copy = new RemapRule[rules.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = rules[i] ?? throw new ArgumentNullException(
                nameof(rules), FormattableString.Invariant($"Remap rule {i} is null."));
        }

        _rules = copy;
        return this;
    }

    /// <summary>
    /// Orders every type's rows by id and hands back the immutable snapshot. The builder may be used again
    /// afterwards, and each <see cref="Build"/> produces a snapshot that shares nothing mutable with it.
    /// </summary>
    public ContentSnapshot Build()
    {
        var tables = new ContentSnapshotTable[_byType.Count];
        int next = 0;
        foreach (KeyValuePair<ushort, List<Entry>> pair in _byType)
        {
            List<Entry> entries = pair.Value;
            Entry[] ordered = entries.ToArray();

            // By id, ties in the order they were added, so the order rows went in cannot change the snapshot
            // and two rows that share an id keep a stable one between them.
            Array.Sort(ordered, static (left, right) => left.Id == right.Id
                ? left.Order.CompareTo(right.Order)
                : left.Id.CompareTo(right.Id));

            var rows = new ContentRow[ordered.Length];
            var bodies = new ReadOnlyMemory<byte>[ordered.Length];
            for (int i = 0; i < ordered.Length; i++)
            {
                rows[i] = ordered[i].Row;
                bodies[i] = ordered[i].Body;
            }

            tables[next++] = new ContentSnapshotTable(new ContentTypeId(pair.Key), rows, bodies);
        }

        return new ContentSnapshot(_identity, _rules, tables);
    }

    /// <summary>One added row, its body and the position it was added at, which is its tie break.</summary>
    readonly struct Entry(ContentRow row, ReadOnlyMemory<byte> body, int order)
    {
        public ContentRow Row { get; } = row;

        public ReadOnlyMemory<byte> Body { get; } = body;

        public int Order { get; } = order;

        public int Id => Row.Id;
    }
}
