using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One published version's row (spec 4.3's <c>catalog_version</c>): the number, BOTH manifest hashes, the
/// minimum builds, the format generation, the base it was published from, who published it, the note and
/// when. It is what <c>catalog-versions</c> lists and what a pin is checked against.
/// <para>
/// A published version is IMMUTABLE from the moment its transaction commits, and there is no sealed flag
/// here because every table a publish writes is append only or temporal, so a flag saying so would be a
/// second representation of a fact the schema already guarantees. Holding a version back from a restart is
/// the operator's PIN, which is a store-level value rather than a property of this row.
/// </para>
/// <para>
/// <b>Two hashes, because a pack has two sides.</b> The server manifest names every chunk including the
/// server-only ones, and the client manifest omits them entirely, so the two digests differ whenever any
/// type or any field is server only. The connect door carries the CLIENT one (contracts 7.5).
/// </para>
/// </summary>
/// <param name="VersionNumber">The monotonic version number, from 1, never reused and never skipped.</param>
/// <param name="ServerManifestHash">The server manifest digest, lower hex, 64 characters.</param>
/// <param name="ClientManifestHash">The client manifest digest, lower hex, 64 characters.</param>
/// <param name="MinimumServerBuild">Consumer supplied, compared and never interpreted (contracts 7.4).</param>
/// <param name="MinimumClientBuild">Consumer supplied, compared and never interpreted.</param>
/// <param name="FormatGeneration">The engine's own pack format generation, never supplied by a caller.</param>
/// <param name="BaseVersion">The version this one was published from, or 0 for the first.</param>
/// <param name="PublishedBy">The identity that published it, at most 128 characters.</param>
/// <param name="Note">The publisher's note, at most 1,024 characters, empty when none.</param>
/// <param name="PublishedAtUtc">When the publish transaction committed.</param>
public sealed record ContentVersionRecord(
    int VersionNumber,
    string ServerManifestHash,
    string ClientManifestHash,
    int MinimumServerBuild,
    int MinimumClientBuild,
    int FormatGeneration,
    int BaseVersion,
    string PublishedBy,
    string Note,
    DateTimeOffset PublishedAtUtc)
{
    /// <summary>The number and the SERVER manifest hash, the pair a server boot names a version by.</summary>
    public ContentVersionIdentity ServerIdentity => new(VersionNumber, ServerManifestHash);

    /// <summary>The number and the CLIENT manifest hash, the pair the connect door carries.</summary>
    public ContentVersionIdentity ClientIdentity => new(VersionNumber, ClientManifestHash);
}
