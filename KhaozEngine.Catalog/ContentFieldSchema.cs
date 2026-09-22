using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// The value kinds a content field may carry, contracts 4.7.
/// <para>
/// The NUMBERING is durable and is written out explicitly rather than left to declaration order, because
/// <c>catalog_row_field.field_kind</c> stores it under a <c>CHECK (field_kind BETWEEN 0 AND 6)</c> (spec
/// 4.4). Inserting a kind in the middle would restate every authored row.
/// </para>
/// <para>
/// There is no asset-reference kind: contracts 4.7 describes one and spec 3.3 settles the item type's
/// <c>icon</c>, <c>mesh</c> and <c>held_mesh</c> as <see cref="OpaqueBytes"/> with a declared shape, with
/// CCR-1 of spec 20 carrying the request for a kind of its own.
/// </para>
/// </summary>
public enum ContentFieldKind
{
    /// <summary>A plain integer.</summary>
    Int = 0,

    /// <summary>An integer times a fixed scale, contracts 13.1. There is no float anywhere in content.</summary>
    ScaledInt = 1,

    /// <summary>A boolean, stored as 0 or 1.</summary>
    Bool = 2,

    /// <summary>A definition id pointing at a row of the content type its reference target names.</summary>
    KeyReference = 3,

    /// <summary>An ORDERED list of tag ids, authored order preserved and never sorted (contracts 4.6).</summary>
    TagList = 4,

    /// <summary>A MARKER carrying no value and no bytes. The key is derived, contracts 12.1.</summary>
    LocalizedTextKey = 5,

    /// <summary>Bytes the engine neither reads nor interprets, including an asset reference.</summary>
    OpaqueBytes = 6,
}

/// <summary>
/// One field of one content type's schema, contracts 4.7. Three readers need it: a game's admin console
/// renders a generic editor from it, the authoring store's audit records field-level before and after values
/// through it, and the publish diff is computed over it.
/// </summary>
/// <param name="Name">The field name, and the name contracts 12.1 derives a localization key from.</param>
/// <param name="Kind">The value kind.</param>
/// <param name="ReferenceTarget">
/// The content type KEY a <see cref="ContentFieldKind.KeyReference"/> points at, or
/// <see cref="TagReferenceTarget"/> for a <see cref="ContentFieldKind.TagList"/>. Null for every other kind.
/// </param>
/// <param name="Visibility">Whether a client ever sees the value, contracts 11.</param>
/// <param name="Required">Whether a live row must carry a value.</param>
/// <param name="Scale">
/// The fixed scale of a <see cref="ContentFieldKind.ScaledInt"/>, so the stored integer is the value times
/// this number. Every other kind carries 1.
/// </param>
public sealed record ContentFieldEntry(
    string Name,
    ContentFieldKind Kind,
    string? ReferenceTarget,
    ContentVisibility Visibility,
    bool Required,
    int Scale = 1)
{
    /// <summary>The reference target a tag list declares, which says only that its ids are tag ids.</summary>
    public const string TagReferenceTarget = "tag";

    /// <summary>
    /// A localized text key is a MARKER: no value in the row, no bytes in the chunk (spec 3.2). It still
    /// occupies a schema slot, because the editor shows the derived key read only beside the field, the
    /// audit and the diff need to see a type gain a text field, and the text chunk builder needs to know
    /// which strings the row owns.
    /// </summary>
    public bool IsDerivedMarker => Kind == ContentFieldKind.LocalizedTextKey;
}

/// <summary>
/// One content type's ORDERED field list. A row's values are parallel to it by INDEX rather than keyed by
/// name, so a codec is a positional walk and the schema index and the row index are the same number.
/// <para>
/// A schema also declares its <see cref="BaselineFieldCount"/>, the number of fields the type had when it
/// FIRST SHIPPED. Everything at or after that index was appended by a later engine release, and
/// <c>ContentRowTailRule</c> is the one place that says what the split means to a row's bytes. A schema that
/// declares no baseline is entirely baseline, which is every type that has never gained a field and is why
/// nothing else in the tree had to change.
/// </para>
/// </summary>
public sealed class ContentFieldSchema
{
    readonly Dictionary<string, ContentFieldEntry> _byName;

    /// <summary>Builds a schema, checking every entry against the rules of contracts 4.7.</summary>
    /// <param name="fields">The fields in declared order.</param>
    /// <param name="baselineFieldCount">
    /// How many leading fields shipped with the type, or null when every field did. An APPENDED field must be
    /// optional: a required one could not be absent from an older row, so a body that never carried it would
    /// have to be refused and the whole rule would buy nothing.
    /// </param>
    /// <exception cref="ArgumentException">A name is blank or repeated, or a reference target does not match its kind.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A scale is below 1, is not 1 on a kind that is not scaled, or the baseline is outside the field list.</exception>
    public ContentFieldSchema(IReadOnlyList<ContentFieldEntry> fields, int? baselineFieldCount = null)
    {
        ArgumentNullException.ThrowIfNull(fields);

        int baseline = baselineFieldCount ?? fields.Count;
        if (baseline < 0 || baseline > fields.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baselineFieldCount),
                baseline,
                FormattableString.Invariant(
                    $"A schema of {fields.Count} fields cannot declare {baseline} of them as its first release."));
        }

        var ordered = new ContentFieldEntry[fields.Count];
        _byName = new Dictionary<string, ContentFieldEntry>(fields.Count, StringComparer.Ordinal);
        for (int i = 0; i < fields.Count; i++)
        {
            ContentFieldEntry entry = fields[i] ?? throw new ArgumentException(
                FormattableString.Invariant($"Field entry {i} is null."), nameof(fields));
            Check(entry, i, nameof(fields));
            if (!_byName.TryAdd(entry.Name, entry))
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"Field name '{entry.Name}' is declared twice, at index {i}."),
                    nameof(fields));
            }

            if (i >= baseline && entry.Required && !entry.IsDerivedMarker)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Field '{entry.Name}' at index {i} was appended after this type's first {baseline} fields and is declared required. An appended field is absent from every row written before it existed, so only an optional one can be appended."),
                    nameof(fields));
            }

            ordered[i] = entry;
        }

        Fields = ordered;
        BaselineFieldCount = baseline;
    }

    /// <summary>The fields in DECLARED order, which is the order a row's values are parallel to.</summary>
    public IReadOnlyList<ContentFieldEntry> Fields { get; }

    /// <summary>
    /// How many leading fields shipped with the type. Fields from this index on were APPENDED by a later
    /// engine release, are optional, and are the only ones a row's bytes may end before.
    /// </summary>
    public int BaselineFieldCount { get; }

    /// <summary>Looks a field up by name, ORDINALLY, the way every other key comparison in the catalog works.</summary>
    public bool TryGet(string name, [MaybeNullWhen(false)] out ContentFieldEntry entry)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out entry);
    }

    static void Check(ContentFieldEntry entry, int index, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"Field entry {index} has no name."), parameterName);
        }

        switch (entry.Kind)
        {
            case ContentFieldKind.KeyReference when string.IsNullOrWhiteSpace(entry.ReferenceTarget):
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Field '{entry.Name}' is a key reference and names no content type key."),
                    parameterName);
            case ContentFieldKind.TagList when
                !string.Equals(entry.ReferenceTarget, ContentFieldEntry.TagReferenceTarget, StringComparison.Ordinal):
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Field '{entry.Name}' is a tag list, so its reference target must be '{ContentFieldEntry.TagReferenceTarget}'."),
                    parameterName);
            case ContentFieldKind.KeyReference:
            case ContentFieldKind.TagList:
                break;
            default:
                if (entry.ReferenceTarget is not null)
                {
                    throw new ArgumentException(
                        FormattableString.Invariant(
                            $"Field '{entry.Name}' is a {entry.Kind} and carries a reference target, which only a key reference and a tag list do."),
                        parameterName);
                }

                break;
        }

        if (entry.Kind == ContentFieldKind.ScaledInt)
        {
            if (entry.Scale < 1)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    entry.Scale,
                    FormattableString.Invariant($"Field '{entry.Name}' declares a scale below 1."));
            }
        }
        else if (entry.Scale != 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                entry.Scale,
                FormattableString.Invariant(
                    $"Field '{entry.Name}' is a {entry.Kind} and carries a scale of {entry.Scale}, which only a scaled int does."));
        }
    }
}
