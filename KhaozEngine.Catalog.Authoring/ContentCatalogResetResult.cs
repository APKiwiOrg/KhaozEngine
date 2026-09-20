using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What one catalog RESET destroyed, and the store epoch the recreated schema minted in its place. Every
/// provider's reset answers with this, so an operator console prints one line whichever backend it drove.
/// <para>
/// <b>The hashes are the ones that STOOD, which is the only moment they can be read.</b> A reset drops
/// <c>catalog_version</c> with everything else, so after it returns there is no query that can answer what
/// the store used to serve. A caller replacing content at the same version number needs exactly those two
/// hashes to tell whether a pack root on disk is the old content or the new, and this record is the last
/// place they exist.
/// </para>
/// <para>
/// <b><see cref="StoreEpoch"/> is NEW.</b> The reset recreates the schema through the same script the
/// initializer runs, and that script mints a fresh epoch. A reset store is a new store: it shares no history
/// with the one it replaced, so a durable page stamped against the old epoch must not be taken for a page of
/// this one.
/// </para>
/// </summary>
/// <param name="ActiveVersion">The version the store served before the reset, or 0 when it had published nothing.</param>
/// <param name="ServerManifestHash">The active version's server manifest hash, or null when nothing was published.</param>
/// <param name="ClientManifestHash">The active version's client manifest hash, or null when nothing was published.</param>
/// <param name="VersionsDropped">How many rows <c>catalog_version</c> held.</param>
/// <param name="RowsDropped">How many rows <c>catalog_row</c> held, every revision counted.</param>
/// <param name="StoreEpoch">The epoch the recreated schema minted, which no earlier version shares.</param>
public sealed record ContentCatalogResetResult(
    int ActiveVersion,
    string? ServerManifestHash,
    string? ClientManifestHash,
    int VersionsDropped,
    int RowsDropped,
    string StoreEpoch)
{
    /// <summary>
    /// The one line an operator reads, and the same text the reset files in the new store's audit, so the
    /// console output and the stored record cannot say two different things.
    /// </summary>
    public string Summary => FormattableString.Invariant(
        $"Catalog reset. It stood at {Stood}, and {VersionsDropped} versions and {RowsDropped} row revisions were dropped. The new store epoch is {StoreEpoch}.");

    /// <summary>What the store served, named by number and by both hashes when it served anything.</summary>
    string Stood => ServerManifestHash is null || ClientManifestHash is null
        ? "nothing published"
        : FormattableString.Invariant(
            $"version {ActiveVersion}, server manifest {ServerManifestHash}, client manifest {ClientManifestHash}");
}
