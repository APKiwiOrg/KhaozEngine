using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>Why a rebuild refused, and the detail an operator reads the two digests off.</summary>
/// <param name="Reason">One of <see cref="ContentPackRebuild"/>'s refusal constants.</param>
/// <param name="Detail">What differed, naming the rebuilt value and the recorded one.</param>
readonly record struct ContentRebuildRefusal(string Reason, string Detail);

/// <summary>
/// The check that makes a rebuild safe to write: both rebuilt manifests are digested and compared against the
/// two hashes the version record holds, and a difference in either one refuses the whole rebuild.
/// <para>
/// <b>Two comparisons pin the entire closure.</b> The canonical manifest text digests the version number, the
/// format generation, both minimum builds, the type list, EVERY chunk hash inline, the rule chunk hash and
/// the languages (contracts 7.3). So a rebuilt pack whose manifest digests to the recorded value cannot be
/// carrying a chunk the published version did not, at a hash the published version did not name, and there is
/// nothing left over for a per-chunk check to catch.
/// </para>
/// <para>
/// <b>It runs BEFORE the first write, and that is the property rather than an optimisation.</b> A rebuild
/// writes into a store a boot will read, so a refusal that had already filed half a pack would leave objects
/// a later rebuild or sweep has to reason about. Verifying first means a refused rebuild leaves the target
/// exactly as it found it.
/// </para>
/// <para>
/// <b>Stored bytes and stored sizes are never compared, because they are allowed to differ.</b> A chunk's
/// hash is over the UNCOMPRESSED canonical bytes and its 36 byte header carries no version number, so the
/// same rows compressed by a different build are a different FILE at the same content address. Comparing
/// files would report a difference where the format says there is none.
/// </para>
/// </summary>
static class ContentRebuildVerification
{
    /// <summary>
    /// The refusal, or null when both digests match what the version record holds.
    /// </summary>
    /// <param name="record">The published version's row, which holds both recorded digests.</param>
    /// <param name="server">The rebuilt server manifest.</param>
    /// <param name="client">The rebuilt client manifest.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ContentRebuildRefusal? Check(
        ContentVersionRecord record,
        ContentManifest server,
        ContentManifest client)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);

        string rebuiltServer = ContentManifestText.Hash(server);
        if (!string.Equals(rebuiltServer, record.ServerManifestHash, StringComparison.Ordinal))
        {
            return new ContentRebuildRefusal(
                ContentPackRebuild.RefusedServerManifest,
                Detail("server", record.VersionNumber, rebuiltServer, record.ServerManifestHash));
        }

        string rebuiltClient = ContentManifestText.Hash(client);
        if (!string.Equals(rebuiltClient, record.ClientManifestHash, StringComparison.Ordinal))
        {
            return new ContentRebuildRefusal(
                ContentPackRebuild.RefusedClientManifest,
                Detail("client", record.VersionNumber, rebuiltClient, record.ClientManifestHash));
        }

        return null;
    }

    /// <summary>
    /// The refusal text. It names BOTH digests, because "the manifest does not match" sends a reader to
    /// compare two things they cannot see, and the pair is what tells them whether the rows moved or the
    /// registry did.
    /// </summary>
    static string Detail(string side, int versionNumber, string rebuilt, string recorded)
        => FormattableString.Invariant(
            $"The rebuilt {side} manifest digests to {rebuilt} and version {versionNumber} records {recorded}. The rows, the rules or the registry the rebuild read are not the ones the version was published from, so nothing was written.");
}
