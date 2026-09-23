using System;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The position of a field of one of the ENGINE's own types, resolved off the live registry at the moment a
/// rule needs it.
/// </summary>
/// <remarks>
/// <b>A validator here may not index an engine row off a schema it built itself.</b> Two of these rules read
/// an <c>item</c> row, and the tempting shape is a static readonly index taken from
/// <c>ItemContentType.CreateSchema()</c> once at type load. That number is the index in THIS build's idea of
/// the item type, not in the one the candidate was authored and registered against, so an engine release
/// that appends or reorders an item field leaves the rule reading its neighbour. Nothing fails: the wrong
/// number is a legal number, and the rule quietly reports a defect that is not there or passes one that is.
/// <para>
/// So the index comes off <see cref="ContentTypeRegistry"/> at validation time, through
/// <see cref="ContentFieldSchema.IndexOf"/>, which is the same schema the registration and the codec were
/// built from. A sweep resolves each one ONCE and then walks its rows, so the lookup is per run rather than
/// per row.
/// </para>
/// <para>
/// It answers -1 rather than throwing for a type or a field the registry does not carry. A game that
/// registered no <c>item</c> type has no item rows for the rule to hold anything against, which is a rule
/// with nothing to say rather than a content defect, and <see cref="ContentFieldLookup"/>'s refusal is for a
/// READER that cannot proceed without the field.
/// </para>
/// </remarks>
static class EngineSchemaFields
{
    /// <summary>
    /// The index of <paramref name="fieldName"/> in the schema registered under <paramref name="typeKey"/>,
    /// or -1 when the registry carries no such type or that type declares no such field.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal static int IndexIn(ContentTypeRegistry registry, string typeKey, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(typeKey);
        ArgumentNullException.ThrowIfNull(fieldName);

        return registry.TryGetByKey(typeKey, out ContentTypeRegistration? registration)
            ? registration.Schema.IndexOf(fieldName)
            : -1;
    }
}
