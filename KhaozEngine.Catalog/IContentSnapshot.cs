using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// The narrow READ side every consumer outside this package is written against, so a game and the item
/// instances layer compile against an INTERFACE rather than against whichever concrete holder is live. Both
/// <see cref="ContentSnapshot"/> (a candidate, or a version loaded but not yet active) and
/// <c>ContentRuntime</c> (the active one) implement it, which is what lets the validator, a test and the
/// running server all be handed the same shape.
/// <para>
/// SEVEN members and no more, which a reflection test pins: it carries no authoring concept, no chunk, no
/// hash beyond the version identity pair and no mutation, so a client holds one with the pure read graph of
/// spec 2.1. <see cref="TryGetRow"/> is the generic path and <see cref="ItemRow"/> is the typed one over the
/// same storage, reached through the concrete holder rather than through this seam, because a typed view
/// over a schema the engine does not own could not be written and one type does not belong on the shape
/// every type is read through.
/// </para>
/// </summary>
public interface IContentSnapshot
{
    /// <summary>The pack's version number, contracts 7.1, which is what a durable page stamps.</summary>
    int VersionNumber { get; }

    /// <summary>The number and its manifest hash, the pair that travels together everywhere.</summary>
    ContentVersionIdentity Identity { get; }

    /// <summary>One row by id, or false when this version carries no row under that id.</summary>
    bool TryGetRow(ContentTypeId type, int id, [MaybeNullWhen(false)] out ContentRow row);

    /// <summary>One id by key, compared ORDINALLY over the UTF-8 bytes, contracts 5.3.</summary>
    bool TryGetId(ContentTypeId type, ContentKey key, out int id);

    /// <summary>Every row of one type, ordered by id, and empty for a type this version carries nothing for.</summary>
    IReadOnlyList<ContentRow> Rows(ContentTypeId type);

    /// <summary>The full ordered rule set, contracts 8.1, in sequence order.</summary>
    IReadOnlyList<RemapRule> Rules { get; }

    /// <summary>The retired bit of spec 3.9, answered without walking or decoding the row.</summary>
    bool IsRetired(ContentTypeId type, int id);
}
