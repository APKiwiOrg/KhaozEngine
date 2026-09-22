using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>
/// Step 3's FIRST check, and the one nothing later in the boot can make for itself. The pointer is the only
/// object in a content-addressed store that is not named by its own hash, so a pack root left behind by a
/// catalog REPLACED at the same version number answers every later check perfectly: the manifest digests to
/// its own name, declares the right number, decodes, and every chunk verifies. Only the version record knows
/// which manifest that number is supposed to mean, and the connect door would meanwhile be advertising the
/// record's client hash over the old pack's rows.
/// <para>
/// It REFUSES rather than treating the pointer as absent. The recovery that repairs an absent pointer is the
/// CALLER's, and the caller reads the pointer itself, through the pack store, and rebuilds the version only
/// when that read answers null. A stale pointer is not null there either, so a boot pretending it was absent
/// would move nothing and would write a line naming no hashes. This one names the version and BOTH hashes,
/// which is what tells an operator to rebuild the root rather than to go looking for a lost file.
/// </para>
/// <para>
/// Both halves are compared, though the boot fetches only the server manifest, because the pointer's client
/// half is what a publish sweep builds a version's keep set out of and half a stale pointer is a stale
/// pointer. A boot with no hash source has no fact to compare against and skips the check, which is the boot
/// exactly as it ran before.
/// </para>
/// </summary>
internal static class ContentBootPointerCheck
{
    /// <summary>
    /// Where the version record's hashes come from: <see cref="ContentBootOptions.VersionHashes"/> when the
    /// host set it, otherwise <see cref="ContentBootOptions.Directory"/> only when that is itself a hash
    /// source, otherwise nowhere.
    /// </summary>
    /// <param name="options">The boot's options.</param>
    public static IContentVersionHashSource? SourceOf(ContentBootOptions options)
        => options.VersionHashes ?? options.Directory as IContentVersionHashSource;

    /// <summary>
    /// The comparison: a refusal when the record disagrees or its read throws, and otherwise whether a
    /// comparison ran at all, so the boot can report <see cref="ContentBootResult.PackPointerCrossChecked"/>.
    /// </summary>
    /// <param name="options">The boot's options.</param>
    /// <param name="version">The version the boot resolved.</param>
    /// <param name="pointer">The pack store's pointer for that version.</param>
    /// <param name="cancellationToken">Cancels the record read.</param>
    public static async Task<Outcome> RunAsync(
        ContentBootOptions options,
        int version,
        PackVersionPointer pointer,
        CancellationToken cancellationToken)
    {
        IContentVersionHashSource? source = SourceOf(options);
        if (source is null)
        {
            return Outcome.NotChecked;
        }

        ContentVersionHashes? recorded;
        try
        {
            recorded = await source.GetVersionHashesAsync(version, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception fault) when (ContentBootSourceFault.IsFault(fault, cancellationToken))
        {
            return new Outcome(
                ContentBootSourceFault.Refuse(
                    3,
                    FormattableString.Invariant($"version record for version {version}"),
                    fault),
                CrossChecked: false);
        }

        if (recorded is not ContentVersionHashes hashes)
        {
            return Outcome.NotChecked;
        }

        if (!string.Equals(pointer.ServerManifestHash, hashes.ServerManifestHash, StringComparison.Ordinal))
        {
            return new Outcome(
                Refusal(options, version, "server", pointer.ServerManifestHash, hashes.ServerManifestHash),
                CrossChecked: false);
        }

        return string.Equals(pointer.ClientManifestHash, hashes.ClientManifestHash, StringComparison.Ordinal)
            ? new Outcome(null, CrossChecked: true)
            : new Outcome(
                Refusal(options, version, "client", pointer.ClientManifestHash, hashes.ClientManifestHash),
                CrossChecked: false);
    }

    /// <summary>What the check did: a refusal, or null and whether the pointer was compared and agreed.</summary>
    /// <param name="Refusal">The stale pointer's refusal, or null when the boot goes on.</param>
    /// <param name="CrossChecked">True only when the comparison ran and both halves agreed.</param>
    public readonly record struct Outcome(ContentBootResult? Refusal, bool CrossChecked)
    {
        /// <summary>No source, or no record: nothing was compared and nothing refused.</summary>
        public static Outcome NotChecked => new(null, CrossChecked: false);
    }

    /// <summary>The stale pointer's line: which version, which store, which side, and the two hashes.</summary>
    static ContentBootResult Refusal(
        ContentBootOptions options,
        int version,
        string side,
        string pointed,
        string recorded)
        => ContentBootResult.Refuse(
            ContentBootRefusal.PackPointerMismatch,
            3,
            FormattableString.Invariant(
                $"{ContentBoot.LinePrefix}version {version} in {options.StoreName} points at {side} manifest {pointed}, ")
                + FormattableString.Invariant($"the version record names {recorded}."));
}
