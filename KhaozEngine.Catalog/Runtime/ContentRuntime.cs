using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// The loaded ACTIVE version, spec 9.1: one <c>ContentTypeTable</c> per registered type, the four derived
/// indexes of 9.4 over them, and the seven members of <see cref="IContentSnapshot"/> answered out of those
/// arrays. Built once at boot, immutable after, and swapped whole through
/// <see cref="ContentRuntimeHolder"/> when a version changes.
/// <para>
/// <b>It is not an alternative to <see cref="ContentSnapshot"/>, it is the next step after one.</b> The
/// snapshot is the CANDIDATE shape: a publish builds one and validates it, a test builds one by hand, and
/// the pack reader assembles one out of the chunks it decoded. The runtime is the ACTIVE shape: it takes a
/// snapshot, indexes it by id into the arrays spec 9.1 describes, and is what a server reads for the rest of
/// the process. So the path is one way and it is
/// <see cref="FromSnapshot"/>: reader to snapshot to runtime to holder. Nothing converts back, because
/// nothing needs to.
/// </para>
/// <para>
/// The hand-off SHARES the snapshot's per-type body blob rather than copying it, which is what keeps boot's
/// peak at one copy of the catalog: the snapshot already concatenated every body in ascending id order,
/// which is exactly what spec 9.2 asks the runtime to hold, so the runtime allocates its index arrays and
/// nothing else. Both are immutable, so the sharing is invisible.
/// </para>
/// <para>
/// Decode is EAGER here, because the validator runs on the full snapshot and because a server that decoded
/// lazily would pay a first-touch cost inside a tick (spec 9.3). The lazy half of that rule belongs to the
/// CLIENT and lives in <see cref="ContentPackReader.ReadRowAsync"/>.
/// </para>
/// </summary>
public sealed class ContentRuntime : IContentSnapshot
{
    static readonly ContentRow[] NoRows = [];

    readonly Dictionary<ushort, ContentTypeTable> _tables;
    readonly ContentTypeId[] _types;
    readonly RemapRule[] _rules;
    readonly ContentTypeTable? _items;

    /// <summary>
    /// The registered load indexes of spec 9.4, or null until <see cref="BuildLoadIndexes"/> has run them.
    /// The ONE field here that is not readonly, and it is assigned exactly once: null IS the "still building"
    /// state, which is what makes "an index may not read another index" a refusal rather than a comment.
    /// </summary>
    Dictionary<ushort, IContentLoadIndex>? _loadIndexes;

    ContentRuntime(
        ContentVersionIdentity identity,
        ContentTypeRegistry registry,
        RemapRule[] rules,
        ContentTypeTable[] tables)
    {
        Identity = identity;
        Registry = registry;
        _rules = rules;
        _tables = new Dictionary<ushort, ContentTypeTable>(tables.Length);
        _types = new ContentTypeId[tables.Length];
        for (int i = 0; i < tables.Length; i++)
        {
            ContentTypeTable table = tables[i];
            _tables.Add(table.Type.Value, table);
            _types[i] = table.Type;
        }

        // The item table is hoisted, because budget P7 is one array index into Offsets plus one span slice
        // and a dictionary probe per call is not part of that. Every other type's table is hoisted by its
        // own reader the same way, once, rather than per lookup.
        _tables.TryGetValue(EngineContentTypes.ItemTypeId, out _items);
        Indexes = ContentDerivedIndexes.Build(this);
    }

    /// <summary>
    /// Boot step 7: index a decoded snapshot by id into the arrays of spec 9.1 and derive the four engine
    /// indexes of 9.4 over them. Step 7b is <see cref="BuildLoadIndexes"/> and is a separate call, because
    /// the boot runs it after these four and before the validator.
    /// </summary>
    /// <param name="snapshot">The decoded version, which the pack reader assembles.</param>
    /// <param name="registry">The registry the snapshot's types came from, FROZEN by the pack load.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public static ContentRuntime FromSnapshot(ContentSnapshot snapshot, ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registry);

        ContentSnapshotTable[] source = snapshot.Tables();
        var tables = new ContentTypeTable[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            ContentSnapshotTable table = source[i];
            tables[i] = new ContentTypeTable(
                table.Type,
                table.RowArray,
                table.BodyBlob,
                table.BodyOffsets,
                table.BodyLengths);
        }

        var rules = new RemapRule[snapshot.Rules.Count];
        for (int i = 0; i < rules.Length; i++)
        {
            rules[i] = snapshot.Rules[i];
        }

        return new ContentRuntime(snapshot.Identity, registry, rules, tables);
    }

    /// <inheritdoc />
    public int VersionNumber => Identity.Number;

    /// <inheritdoc />
    public ContentVersionIdentity Identity { get; }

    /// <inheritdoc />
    public IReadOnlyList<RemapRule> Rules => _rules;

    /// <summary>The registry this version loaded against, which is what supplies a type's schema and codec.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>Every content type this version carries a row for, ASCENDING by type id.</summary>
    public IReadOnlyList<ContentTypeId> Types => _types;

    /// <summary>The four derived indexes of spec 9.4, built eagerly at load and immutable after.</summary>
    public ContentDerivedIndexes Indexes { get; }

    /// <summary>True once <see cref="BuildLoadIndexes"/> has run every registered index to completion.</summary>
    public bool LoadIndexesBuilt => _loadIndexes is not null;

    /// <inheritdoc />
    public bool TryGetRow(ContentTypeId type, int id, [MaybeNullWhen(false)] out ContentRow row)
    {
        if (_tables.TryGetValue(type.Value, out ContentTypeTable? table))
        {
            row = table.Row(id);
            return row is not null;
        }

        row = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetId(ContentTypeId type, ContentKey key, out int id) => TryGetId(type, key.Utf8, out id);

    /// <summary>
    /// One id by key over the raw UTF-8 bytes, which is the form the open-addressed index of spec 9.4 probes
    /// and the one a caller holding a slice already can use without building anything.
    /// </summary>
    public bool TryGetId(ContentTypeId type, ReadOnlySpan<byte> key, out int id)
    {
        if (_tables.TryGetValue(type.Value, out ContentTypeTable? table))
        {
            return table.TryGetId(key, out id);
        }

        id = 0;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentRow> Rows(ContentTypeId type)
        => _tables.TryGetValue(type.Value, out ContentTypeTable? table) ? table.Rows : NoRows;

    /// <inheritdoc />
    public bool IsRetired(ContentTypeId type, int id)
        => _tables.TryGetValue(type.Value, out ContentTypeTable? table) && table.IsRetired(id);

    /// <summary>True when this version carries a row under that id, retired or not.</summary>
    public bool HasRow(ContentTypeId type, int id)
        => _tables.TryGetValue(type.Value, out ContentTypeTable? table) && table.HasRow(id);

    /// <summary>
    /// The row's encoded body, which is the lookup spec 9.1 describes: one array read and one span slice, no
    /// dictionary beyond resolving the TYPE, no lock and no allocation. A reader on a hot path resolves the
    /// type once and keeps the answer, the way the item view does.
    /// </summary>
    public ReadOnlySpan<byte> Body(ContentTypeId type, int id)
        => _tables.TryGetValue(type.Value, out ContentTypeTable? table) ? table.Body(id) : default;

    /// <summary>The row's key as a slice of the loaded bytes, with no copy and no string materialised.</summary>
    public ContentKey Key(ContentTypeId type, int id)
        => _tables.TryGetValue(type.Value, out ContentTypeTable? table) ? table.Key(id) : default;

    /// <summary>
    /// The typed <c>item</c> view of spec 9.1, through the hoisted item table: one array index, one span
    /// slice and a fixed walk, with no allocation. Budget P7's own path.
    /// </summary>
    public bool TryGetItem(int id, out ItemRow row)
    {
        ContentTypeTable? items = _items;
        if (items is null || (uint)id >= (uint)items.Offsets.Length || items.Offsets[id] < 0)
        {
            row = default;
            return false;
        }

        return ItemRow.TryDecode(items.Bodies, items.Offsets[id], items.Lengths[id], items.IsRetired(id), out row);
    }

    /// <summary>
    /// Boot step 7b (spec 9.4): every registered <see cref="IContentLoadIndex"/>, in TYPE ID ORDER, after the
    /// engine's four and before the validator.
    /// <para>
    /// An index MAY read another type's rows through the snapshot it is handed, which is this runtime, and it
    /// MAY NOT read another index: <see cref="TryGetLoadIndex{T}"/> throws for the whole of this call, so the
    /// rule is a refusal rather than a comment and a registration order can never become load bearing. An
    /// index that throws takes the boot with it, with its type named, rather than leaving a partial index
    /// behind.
    /// </para>
    /// </summary>
    /// <exception cref="ContentLoadIndexException">A registered index threw, or this ran twice.</exception>
    public void BuildLoadIndexes()
    {
        if (_loadIndexes is not null)
        {
            throw new ContentLoadIndexException(
                "The load indexes of this content runtime are built already. They are built ONCE, at boot step 7b, and are immutable after.",
                new ContentTypeId(0),
                string.Empty,
                null);
        }

        var built = new Dictionary<ushort, IContentLoadIndex>();

        // ByTypeId is sorted ascending always, so an index over engine rows is built before one over a later
        // band's and the order never depends on what a host registered first.
        IReadOnlyList<ContentTypeRegistration> registrations = Registry.ByTypeId;
        for (int i = 0; i < registrations.Count; i++)
        {
            ContentTypeRegistration registration = registrations[i];
            IContentLoadIndex? index = registration.LoadIndex;
            if (index is null)
            {
                continue;
            }

            try
            {
                index.Build(this);
            }
            catch (Exception failure)
            {
                throw new ContentLoadIndexException(
                    FormattableString.Invariant(
                        $"The load index for type '{registration.TypeKey}' ({registration.Type.Value}) failed: {failure.Message}"),
                    registration.Type,
                    registration.TypeKey,
                    failure);
            }

            built[registration.Type.Value] = index;
        }

        _loadIndexes = built;
    }

    /// <summary>
    /// The index a type registered, as the type the registering code owns. It answers only AFTER
    /// <see cref="BuildLoadIndexes"/> has finished, so an index cannot read another index while step 7b is
    /// running and no caller can read a half-built one.
    /// </summary>
    /// <typeparam name="T">The registering code's own index type.</typeparam>
    /// <param name="type">The content type the index was registered against.</param>
    /// <param name="index">The built index, or null when this type registered none of that type.</param>
    /// <exception cref="ContentLoadIndexException">Step 7b has not finished.</exception>
    public bool TryGetLoadIndex<T>(ContentTypeId type, [MaybeNullWhen(false)] out T index)
        where T : class, IContentLoadIndex
    {
        Dictionary<ushort, IContentLoadIndex>? built = _loadIndexes
            ?? throw new ContentLoadIndexException(
                FormattableString.Invariant(
                    $"The load index for type {type.Value} was asked for before boot step 7b finished. An index may read another type's ROWS through the snapshot and may not read another index, which would make the registration order load bearing."),
                type,
                string.Empty,
                null);

        if (built.TryGetValue(type.Value, out IContentLoadIndex? found) && found is T typed)
        {
            index = typed;
            return true;
        }

        index = null;
        return false;
    }

    /// <summary>
    /// The managed bytes the loaded tables and the derived indexes hold, which is the memory line of spec
    /// 9.2's arithmetic. It does not count the decoded rows, which the loader allocated before the runtime
    /// existed.
    /// </summary>
    public long ApproximateBytes()
    {
        long bytes = Indexes.ApproximateBytes();
        for (int i = 0; i < _types.Length; i++)
        {
            bytes += _tables[_types[i].Value].ApproximateBytes();
        }

        return bytes;
    }

    /// <summary>One type's loaded table, which is the storage a reader on a hot path hoists once.</summary>
    internal bool TryGetTable(ContentTypeId type, [MaybeNullWhen(false)] out ContentTypeTable table)
        => _tables.TryGetValue(type.Value, out table);
}
