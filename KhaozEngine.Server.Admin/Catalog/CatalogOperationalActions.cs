using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// The operational pair of spec 10.11, the fifteenth and sixteenth actions: <c>catalog-sweep</c> and
/// <c>catalog-verify</c>.
/// <para>
/// <c>catalog-sweep</c> runs step 11 of the publish ALONE, for an operator cleaning up after a crashed
/// publish, and it obeys the same skip-on-listing-failure rule: deleting files on the authority of a
/// listing that failed is how a bad publish turns into a lost pack.
/// </para>
/// <para>
/// <c>catalog-verify</c> walks a version's manifests, fetches every object they name and rehashes it. <b>It
/// is read only and it NEVER repairs</b>, because a repair means deciding which copy is right and only a
/// republish can know that.
/// </para>
/// </summary>
/// <param name="store">The authoring store, whose pack target both actions work against.</param>
/// <param name="registry">The registry a manifest's per-type slot counts are cross-checked against.</param>
internal sealed class CatalogOperationalActions(IContentAuthoringStore store, ContentTypeRegistry registry)
{
    /// <summary>The side name a rule chunk and a text chunk are reported under, since both manifests name them.</summary>
    public const string SharedSide = "shared";

    /// <summary>Registers the two names on the admin surface.</summary>
    /// <param name="admin">The surface to register on.</param>
    public void Register(ServerAdmin admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.RegisterAction(CatalogAdminActions.SweepAction, SweepAsync);
        admin.RegisterAction(CatalogAdminActions.VerifyAction, VerifyAsync);
    }

    /// <summary>
    /// Deletes every pack object no version references. The keep set is the union over EVERY version the
    /// store knows, because a pinned server and a rollback both need older versions to stay fetchable.
    /// </summary>
    async Task<AdminActionResult> SweepAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        if (payload is JsonElement body
            && body.ValueKind == JsonValueKind.Object
            && !CatalogRequest.TryOperator(body, out _, out string? refusal))
        {
            return CatalogRefusal.Malformed(refusal!);
        }

        if (store.PackStore is not IPackStore pack)
        {
            return NoPackStore(CatalogAdminActions.SweepAction);
        }

        IReadOnlyList<ContentVersionRecord> records = await store
            .ListVersionsAsync(cancellationToken).ConfigureAwait(false);
        var versions = new List<int>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            versions.Add(records[i].VersionNumber);
        }

        ContentPackSweepResult result = await ContentPackSweep
            .RunAsync(pack, versions, cancellationToken).ConfigureAwait(false);

        return AdminActionResult.Ok(new CatalogSweepPayload(
            result.Ran, result.Kept, result.Deleted, result.SkipReason));
    }

    /// <summary>
    /// Rehashes every object a version's two manifests name. It verifies BOTH sides rather than one,
    /// because a version has two manifests and a client fetching the client side is as blocked by a corrupt
    /// chunk as a server booting the other.
    /// </summary>
    async Task<AdminActionResult> VerifyAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        int version = 0;
        if (payload is JsonElement body && body.ValueKind == JsonValueKind.Object)
        {
            if (!CatalogRequest.TryVersion(body, "version", out version, out string? refusal))
            {
                return CatalogRefusal.Malformed(refusal!);
            }
        }

        if (version == 0)
        {
            version = await store.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (version == 0)
        {
            return CatalogRefusal.BadRequest(
                "This store has published no version, so there is nothing to verify.",
                ContentAuthoringException.UnknownVersionReason);
        }

        ContentVersionRecord? record = await store.GetVersionAsync(version, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return CatalogRefusal.BadRequest(
                FormattableString.Invariant($"Version {version} does not exist, so there is nothing to verify."),
                ContentAuthoringException.UnknownVersionReason);
        }

        if (store.PackStore is not IPackStore pack)
        {
            return NoPackStore(CatalogAdminActions.VerifyAction);
        }

        var mismatches = new List<CatalogChunkMismatchPayload>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int checkedObjects = 0;

        checkedObjects += await SideAsync(
            pack, record.ServerManifestHash, ContentManifestSide.Server, seen, mismatches, cancellationToken)
            .ConfigureAwait(false);
        checkedObjects += await SideAsync(
            pack, record.ClientManifestHash, ContentManifestSide.Client, seen, mismatches, cancellationToken)
            .ConfigureAwait(false);

        return AdminActionResult.Ok(new CatalogVerifyPayload(
            version, mismatches.Count == 0, checkedObjects, mismatches));
    }

    /// <summary>
    /// One side's manifest and everything it names. An object both manifests name is fetched once, because
    /// rehashing the rule chunk twice would report one defect as two.
    /// </summary>
    async Task<int> SideAsync(
        IPackStore pack,
        string manifestHash,
        ContentManifestSide side,
        HashSet<string> seen,
        List<CatalogChunkMismatchPayload> mismatches,
        CancellationToken cancellationToken)
    {
        string name = side == ContentManifestSide.Server ? "server" : "client";
        ContentManifestRead read = await ContentPackReader
            .ReadManifestAsync(pack, manifestHash, side, registry, cancellationToken).ConfigureAwait(false);
        if (!read.Success || read.Manifest is null)
        {
            mismatches.Add(new CatalogChunkMismatchPayload(
                name, manifestHash, read.Reason ?? ContentPackReader.ReasonFetchFailed));
            return 1;
        }

        int walked = 1;
        ContentManifest manifest = read.Manifest;
        for (int t = 0; t < manifest.Types.Count; t++)
        {
            ManifestTypeEntry type = manifest.Types[t];
            for (int c = 0; c < type.Chunks.Count; c++)
            {
                walked += await ObjectAsync(
                    pack, type.Chunks[c].Hash, name, seen, mismatches, cancellationToken).ConfigureAwait(false);
            }
        }

        for (int l = 0; l < manifest.Languages.Count; l++)
        {
            walked += await ObjectAsync(
                pack, manifest.Languages[l].TextHash, SharedSide, seen, mismatches, cancellationToken)
                .ConfigureAwait(false);
        }

        walked += await ObjectAsync(
            pack, manifest.RemapRuleChunkHash, SharedSide, seen, mismatches, cancellationToken)
            .ConfigureAwait(false);

        return walked;
    }

    /// <summary>One object fetched and rehashed, or skipped when another side already covered it.</summary>
    static async Task<int> ObjectAsync(
        IPackStore pack,
        string hash,
        string side,
        HashSet<string> seen,
        List<CatalogChunkMismatchPayload> mismatches,
        CancellationToken cancellationToken)
    {
        if (!seen.Add(hash))
        {
            return 0;
        }

        ReadOnlyMemory<byte>? file = await pack.GetAsync(hash, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            mismatches.Add(new CatalogChunkMismatchPayload(side, hash, ContentPackReader.ReasonFetchFailed));
            return 1;
        }

        if (!ContentPackReader.TryVerify(file.Value.Span, hash, out string? reason))
        {
            mismatches.Add(new CatalogChunkMismatchPayload(
                side, hash, reason ?? ContentPackReader.ReasonHashMismatch));
        }

        return 1;
    }

    static AdminActionResult NoPackStore(string action)
        => CatalogRefusal.BadRequest(
            FormattableString.Invariant(
                $"{action} works against the pack files a publish wrote, and this store was built with no pack target."),
            ContentAuthoringException.NoPackStoreReason);
}
