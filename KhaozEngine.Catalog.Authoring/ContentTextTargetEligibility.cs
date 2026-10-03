using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The ONE eligibility check a text target meets in every store, before any write: a registered CLIENT type,
/// a declared CLIENT localized text marker, a field name within the provider bound and a derived key within
/// its strict UTF-8 bound. Each store calls it at apply and again for every insert at commit, so every
/// provider refuses the same target with the same reason.
/// </summary>
internal static class ContentTextTargetEligibility
{
    /// <summary>
    /// The longest field name a text target may name. It mirrors the <c>field_name</c> column of the SQLite
    /// <c>catalog_draft_text_edit</c>, <c>catalog_text</c> and <c>catalog_audit</c> tables, which check
    /// <c>length(field_name)</c> at most 64. The bound counts UTF-16 code units, which is never fewer than the
    /// characters SQLite counts, so a name this check accepts always fits the column.
    /// </summary>
    internal const int MaxFieldNameLength = 64;

    /// <summary>The marker field a text target names, after every eligibility and bound check.</summary>
    /// <param name="registry">The store's registry.</param>
    /// <param name="target">The text target.</param>
    /// <exception cref="ContentAuthoringException">The type is unregistered, the field is undeclared or ineligible, or a bound is exceeded.</exception>
    public static ContentFieldEntry Require(ContentTypeRegistry registry, ContentTextTarget target)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(target);

        if (!registry.TryGet(target.Type, out ContentTypeRegistration? registration))
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Content type {target.Type.Value} is not registered, so this store carries no declaration for it."),
                target.Type,
                0,
                ContentAuthoringException.UnknownTypeReason);
        }

        if (!registration.Schema.TryGet(target.FieldName, out ContentFieldEntry? field))
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Content type {target.Type.Value} '{registration.TypeKey}' declares no field named '{target.FieldName}'."),
                target.Type,
                0,
                ContentAuthoringException.UnknownFieldReason);
        }

        if (registration.DefaultVisibility != ContentVisibility.Client
            || !field.IsDerivedMarker
            || field.Visibility != ContentVisibility.Client)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Text needs a CLIENT-visible type and a CLIENT-visible localized text marker, and type {target.Type.Value} '{registration.TypeKey}' field '{target.FieldName}' is not both."),
                target.Type,
                0,
                ContentAuthoringException.TextTargetIneligibleReason);
        }

        if (target.FieldName.Length > MaxFieldNameLength)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Text of type {target.Type.Value} '{registration.TypeKey}' names field '{target.FieldName}', which is {target.FieldName.Length} characters, over the {MaxFieldNameLength} character field name bound."),
                target.Type,
                0,
                ContentAuthoringException.TextBoundsReason);
        }

        int keyBytes = checked(
            ContentTextEdit.MeasureUtf8(registration.TypeKey, nameof(target))
            + 2
            + target.Key.Utf8.Length
            + ContentTextEdit.MeasureUtf8(target.FieldName, nameof(target)));
        if (keyBytes > ContentTextKey.MaxKeyLength)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The derived text key of type {target.Type.Value} row '{target.Key}' field '{target.FieldName}' is {keyBytes} UTF-8 bytes, over the {ContentTextKey.MaxKeyLength} byte bound."),
                target.Type,
                0,
                ContentAuthoringException.TextBoundsReason);
        }

        return field;
    }
}
