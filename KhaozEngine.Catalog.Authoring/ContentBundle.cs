using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One content type as a bundle carries it: its id, its key, the declaration a publish needs, and the
/// ordered field schema an importing store registers it under. The CODEC is not here and cannot be: a codec
/// is code, so an import registers the type against the codec the importing process already holds and uses
/// this row to check that the two declarations agree.
/// </summary>
/// <param name="Type">The stable numeric type id.</param>
/// <param name="TypeKey">The stable string type key.</param>
/// <param name="DefaultVisibility">The visibility a field of this type inherits when it declares none.</param>
/// <param name="ChunkSlots">The id slots per chunk, so a chunk boundary is <c>floor(id / ChunkSlots)</c>.</param>
/// <param name="MaxDefinitionId">The per-type id ceiling, or null when the type declares none.</param>
/// <param name="Schema">The ordered field list a row's values are parallel to.</param>
public sealed record ContentBundleType(
    ContentTypeId Type,
    string TypeKey,
    ContentVisibility DefaultVisibility,
    int ChunkSlots,
    int? MaxDefinitionId,
    ContentFieldSchema Schema);

/// <summary>
/// One live row as a bundle carries it. <b>The id is OPTIONAL and that is the whole of the import id rule</b>
/// (spec 10.9). A row that NAMES an id is imported with it, which is what makes an adoption a no-op for
/// stored data and is contracts 5.1's narrow exception, licensed only into an empty database because nothing
/// is there to collide with. A row that names NO id is allocated one in edit ordinal order, so the bundle's
/// row order determines the ids. Both run through one path and a bundle may mix the two.
/// </summary>
/// <param name="Type">The content type the row belongs to.</param>
/// <param name="Id">The definition id, or null to let the allocator issue one.</param>
/// <param name="Key">The row's key, which is never optional.</param>
/// <param name="IsRetired">Whether the row is retired. A retired row keeps its id and its bytes forever.</param>
/// <param name="FamilyKey">The family the row belongs to, or null when it is allocated from the plain counter.</param>
/// <param name="Fields">The row's fields, by name.</param>
public sealed record ContentBundleRow(
    ContentTypeId Type,
    int? Id,
    ContentKey Key,
    bool IsRetired,
    string? FamilyKey,
    IReadOnlyList<ContentFieldEdit> Fields);

/// <summary>
/// The whole catalog as ONE document (spec 10.9): a format version, the registered type list with their
/// schemas, every live row with its id, key and fields, every family with its blocks, and the full remap
/// rule list. It is the SEEDING format and the LOSSLESS EXPORT format and there is only one of them.
/// <para>
/// Export at version N then import into an empty database reproduces the same rows, the same keys and the
/// SAME IDS, because the export carries them and the import keeps them. That is what makes it lossless
/// rather than an approximation.
/// </para>
/// <para>
/// <b>Import works into an EMPTY database ONLY</b>, empty meaning the version table holds no rows, and a
/// non-empty one is refused with nothing written. That single rule is the answer to a whole class of seeding
/// defect: a seed that runs repeatedly against live data either reverts an operator's value on the next
/// deploy or is beaten forever by a stored row. An import that runs once into an empty database cannot have
/// it, and a deployed database's values change through an edit and a publish and through nothing else.
/// </para>
/// <para>
/// <b>A lossless export is NOT a backup, and the difference is the version LINE.</b> An import republishes
/// at version 1, so the new database's history starts there and every rule's introduced-in is the new line's.
/// A durable page carries the version number it was stamped with, and a rule applies to a stamp strictly
/// older than its own version, so a page stamped 46 against a database whose newest version is 1 is newer
/// than every rule there is. When the version line must be preserved, the path is an ordinary database
/// restore of the authoring store, which is the provider's own tooling and outside this engine.
/// </para>
/// </summary>
public sealed class ContentBundle
{
    /// <summary>The bundle document's own format version, which a reader refuses a mismatch of.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Builds a bundle. Every list is COPIED, so the bundle is immutable once it exists.</summary>
    /// <param name="formatVersion">The bundle document's format version.</param>
    /// <param name="storeEpoch">The identity of the database this was exported from, so two unrelated version 12s are tellable apart.</param>
    /// <param name="sourceVersion">The content version exported, or 0 for a hand-authored seed.</param>
    /// <param name="types">The registered type list with their schemas.</param>
    /// <param name="rows">Every live row.</param>
    /// <param name="families">Every family with its blocks.</param>
    /// <param name="rules">The full ordered remap rule list.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatVersion"/> is below 1, or <paramref name="sourceVersion"/> is negative.</exception>
    public ContentBundle(
        int formatVersion,
        string storeEpoch,
        int sourceVersion,
        IReadOnlyList<ContentBundleType> types,
        IReadOnlyList<ContentBundleRow> rows,
        IReadOnlyList<ContentFamily> families,
        IReadOnlyList<RemapRule> rules)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(formatVersion, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceVersion);
        ArgumentNullException.ThrowIfNull(storeEpoch);

        FormatVersion = formatVersion;
        StoreEpoch = storeEpoch;
        SourceVersion = sourceVersion;
        Types = Copy(types, nameof(types));
        Rows = Copy(rows, nameof(rows));
        Families = Copy(families, nameof(families));
        Rules = Copy(rules, nameof(rules));
    }

    /// <summary>The bundle document's format version.</summary>
    public int FormatVersion { get; }

    /// <summary>The identity of the database this came from, minted once at that database's creation.</summary>
    public string StoreEpoch { get; }

    /// <summary>The content version exported, or 0 for a hand-authored seed with no version behind it.</summary>
    public int SourceVersion { get; }

    /// <summary>The registered type list with their schemas.</summary>
    public IReadOnlyList<ContentBundleType> Types { get; }

    /// <summary>Every live row, in the order the import allocates ids in.</summary>
    public IReadOnlyList<ContentBundleRow> Rows { get; }

    /// <summary>Every family with its blocks.</summary>
    public IReadOnlyList<ContentFamily> Families { get; }

    /// <summary>The full ordered remap rule list, which an import republishes as the new line's rules.</summary>
    public IReadOnlyList<RemapRule> Rules { get; }

    static T[] Copy<T>(IReadOnlyList<T> source, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);

        var copy = new T[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            copy[i] = source[i] ?? throw new ArgumentNullException(
                parameterName, FormattableString.Invariant($"Entry {i} is null."));
        }

        return copy;
    }
}
