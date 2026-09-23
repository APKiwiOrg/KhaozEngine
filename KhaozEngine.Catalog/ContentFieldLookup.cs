using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// Where a NAMED field sits in a row of its type, resolved once against the schema the loaded runtime was
/// built from, and REFUSED when that schema does not carry it.
/// </summary>
/// <remarks>
/// A reader indexes a row by POSITION, because a row's values are parallel by index to its type's schema.
/// The position is not a game's to write down as a literal: a duration field is named for the game's own
/// unit, a reordered or renamed field would leave a number pointing at a neighbour, and a neighbour
/// answers. So a reader names the field and resolves it here, once, at construction.
/// <para>
/// <b>A field the schema lacks is a REFUSAL, not a miss.</b> Answering -1 and letting a caller read it as
/// zero is the dangerous half: a renamed value field prices every row at nothing and the world keeps
/// running and keeps taking trades with nothing in any log. One policy, and the policy is the loud one.
/// </para>
/// <para>
/// It is meant to run at reader CONSTRUCTION, which on a server is boot, before a socket is open, so the
/// refusal lands beside the operator's other content refusals rather than on the first trade of a world that
/// has already accepted players.
/// </para>
/// <para>
/// It is the LOUD half of <see cref="ContentFieldSchema.IndexOf"/>, which answers -1 for a caller that is
/// entitled to ask about a field a type may not carry.
/// </para>
/// </remarks>
public static class ContentFieldLookup
{
    /// <summary>
    /// The position of <paramref name="field"/> in every row of <paramref name="type"/>, under the schema
    /// <paramref name="runtime"/> was built from.
    /// </summary>
    /// <param name="runtime">The loaded active version, whose registry carries the schema.</param>
    /// <param name="type">The content type whose rows will be indexed.</param>
    /// <param name="field">The field name, off the type's own field-name member.</param>
    /// <returns>The index, which is always a real position in a row of that type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> or <paramref name="field"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The runtime does not carry the type, or its schema for the type carries no such field. The message
    /// names both, because the operator's next question is which publish moved which field.
    /// </exception>
    public static int IndexIn(ContentRuntime runtime, ContentTypeId type, string field)
        => IndexIn(runtime, type, field, out _);

    /// <summary>
    /// The position of <paramref name="field"/> and the fixed SCALE its stored integer carries, for a
    /// <see cref="ContentFieldKind.ScaledInt"/> read that has to divide before it answers.
    /// </summary>
    /// <param name="runtime">The loaded active version, whose registry carries the schema.</param>
    /// <param name="type">The content type whose rows will be indexed.</param>
    /// <param name="field">The field name, off the type's own field-name member.</param>
    /// <param name="scale">The schema's own scale, which is 1 for every kind but a scaled integer.</param>
    /// <returns>The index, which is always a real position in a row of that type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> or <paramref name="field"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The runtime does not carry the type, or its schema for the type carries no such field.
    /// </exception>
    /// <remarks>
    /// The scale is READ rather than written down beside the reader, for the reason the position is: a
    /// publish that retunes a field's scale would otherwise leave every answer off by a factor with nothing
    /// failing.
    /// </remarks>
    public static int IndexIn(ContentRuntime runtime, ContentTypeId type, string field, out int scale)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(field);
        scale = 1;

        if (!runtime.Registry.TryGet(type, out ContentTypeRegistration? registration))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Content type {type.Value} is not registered in this runtime, so the '{field}' field a reader over it names cannot be resolved. The world will not run on a catalog it cannot read."));
        }

        ContentFieldSchema schema = registration.Schema;
        int index = schema.IndexOf(field);
        if (index < 0)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Content type '{registration.TypeKey}' carries no field named '{field}' in this runtime's schema, and a reader over it names that field. The world will not run on a catalog it cannot read."));
        }

        scale = schema.Fields[index].Scale;
        return index;
    }
}
