using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What a rebuild did, or why it refused. A refusal is a RESULT rather than a throw, for the same reason a
/// sweep that skipped is: an operator recovering a pack root is going to be told either "the version is in
/// the store now" or "it is not, and here is the digest that did not match", and one of those is not an
/// exceptional condition.
/// </summary>
/// <param name="Rebuilt">True when the version's whole pack is in the target.</param>
/// <param name="VersionNumber">The version the rebuild was asked for.</param>
/// <param name="ChunksBuilt">Chunk rows encoded, at every side, 0 when the builders refused the rows.</param>
/// <param name="ObjectsWritten">Objects actually put, so a rebuild into a full store reports 0.</param>
/// <param name="BytesWritten">Stored bytes actually put, across chunks, the rule chunk and both manifests.</param>
/// <param name="RefusalReason">One of the refusal constants, or null on success.</param>
/// <param name="RefusalDetail">What differed, or null on success.</param>
public sealed record ContentPackRebuildResult(
    bool Rebuilt,
    int VersionNumber,
    int ChunksBuilt,
    int ObjectsWritten,
    long BytesWritten,
    string? RefusalReason,
    string? RefusalDetail);

/// <summary>
/// Rebuilds one PUBLISHED version's whole pack out of the authoring store, verifies it against the manifest
/// hashes that version's record holds, and writes it into a pack store in the publisher's own order.
/// <para>
/// <b>The failure it exists for.</b> The authoring store keeps rows, rules and hashes and never the BYTES, so
/// a server whose pack root does not outlive its process comes back to an empty <see cref="IPackStore"/>
/// while the store still names an active version, and <c>ContentBoot.RunAsync</c> refuses it: the manifest
/// for that version is absent and there is no way to fetch it. Everything needed to write those bytes again
/// is in the database, and this is the operation that does it.
/// </para>
/// <para>
/// <b>It encodes rather than copies, and it is the SAME encoder a publish uses.</b> The rows go through
/// <c>ContentChunkBuilder</c> against an EMPTY baseline, so every chunk the rows occupy is encoded instead of
/// carried forward, and the manifests go through <c>ContentManifestBuilder</c> exactly as step 8 builds them.
/// A second encoder written for this path would be a second thing that has to stay byte-compatible with the
/// format forever.
/// </para>
/// <para>
/// <b>It verifies before it writes, and the verification is the two manifest digests.</b> The canonical
/// manifest text carries every chunk hash inline, so a match pins the whole closure, and a mismatch means the
/// rows or the registry moved since the publish. Nothing a reader could follow is written in that case, not
/// even a chunk, so a refused rebuild leaves the target as it found it.
/// </para>
/// <para>
/// <b>It never calls a mutating member of the authoring store.</b> It reads rows, the publish baseline and
/// one version row, so a rebuild is safe to run against a live store with a draft open.
/// </para>
/// </summary>
public static class ContentPackRebuild
{
    /// <summary>The rebuilt SERVER manifest does not digest to the hash the version record holds.</summary>
    public const string RefusedServerManifest = "server-manifest-mismatch";

    /// <summary>The rebuilt CLIENT manifest does not digest to the hash the version record holds.</summary>
    public const string RefusedClientManifest = "client-manifest-mismatch";

    /// <summary>The builders refused the rows outright, so there was nothing to digest.</summary>
    public const string RefusedCandidateInvalid = "rebuild-candidate-invalid";

    /// <summary>
    /// Reads the version, builds its pack, verifies both digests, and writes.
    /// <para>
    /// The write order is step 9's, deliberately: every chunk, then the rule chunk, then the server manifest,
    /// then the client manifest, then the version POINTER last. A crash part way through therefore leaves
    /// inert content-addressed files and no pointer, which reads as a listing failure and SKIPS the next
    /// sweep rather than authorising it to delete on a partial view.
    /// </para>
    /// </summary>
    /// <param name="store">The authoring store the version is read from, never written to.</param>
    /// <param name="registry">The registry the rows are encoded and the manifests are named through.</param>
    /// <param name="versionNumber">The published version to rebuild.</param>
    /// <param name="target">The pack store the rebuilt files go into.</param>
    /// <param name="pointers">The pointer half, or null to resolve one off <paramref name="target"/>.</param>
    /// <param name="cancellationToken">Cancels the rebuild.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ContentAuthoringException">The store holds no such version, or no pointer half can be resolved.</exception>
    public static async Task<ContentPackRebuildResult> RunAsync(
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        int versionNumber,
        IPackStore target,
        IPackVersionPointerStore? pointers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(target);

        IPackVersionPointerStore pointerStore = pointers
            ?? PackVersionPointers.Resolve(target)
            ?? throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The pack store {target.GetType().Name} writes no version pointer, so a rebuilt version could never be enumerated out of it and the sweep could never find it again. A rebuild target carries both halves, exactly as a publish target does."),
                default,
                0,
                ContentAuthoringException.NoPackStoreReason);

        ContentVersionRecord record = await store
            .GetVersionAsync(versionNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The store holds no version {versionNumber}, so there are no manifest hashes to rebuild against. A rebuild reproduces a PUBLISHED version and cannot invent one."),
                default,
                0,
                ContentAuthoringException.UnknownVersionReason);

        ContentRebuildSnapshot snapshot = await ContentRebuildSnapshot
            .ReadAsync(store, registry, versionNumber, cancellationToken).ConfigureAwait(false);

        ContentRebuiltPack pack;
        try
        {
            pack = Build(registry, record, snapshot);
        }
        catch (ContentAuthoringException refused)
        {
            // Narrow on purpose. The two builders refuse a row whose type the registry does not declare, and
            // the encode check refuses client bytes that still carry a ServerOnly field, and both arrive as
            // this one exception type. Anything else is a defect rather than a rebuild that cannot be made.
            return new ContentPackRebuildResult(
                false, versionNumber, 0, 0, 0, RefusedCandidateInvalid, refused.Message);
        }

        if (ContentRebuildVerification.Check(record, pack.Server, pack.Client) is ContentRebuildRefusal refusal)
        {
            return new ContentPackRebuildResult(
                false, versionNumber, pack.Chunks.Count, 0, 0, refusal.Reason, refusal.Detail);
        }

        (int objects, long bytes) = await WriteAsync(
            target, pointerStore, record, pack, snapshot.Rules, cancellationToken).ConfigureAwait(false);

        return new ContentPackRebuildResult(
            true, versionNumber, pack.Chunks.Count, objects, bytes, null, null);
    }

    /// <summary>
    /// Steps 6 to 8 with no diff and no baseline: every chunk the rows occupy, then both manifests off the
    /// version record's own minimum builds and format-independent numbers.
    /// </summary>
    /// <exception cref="ContentAuthoringException">A row names an unregistered type, or the encode left a <c>ServerOnly</c> field in the client bytes.</exception>
    static ContentRebuiltPack Build(
        ContentTypeRegistry registry,
        ContentVersionRecord record,
        ContentRebuildSnapshot snapshot)
    {
        var findings = new List<ContentFinding>();
        List<ContentChunkRecord> chunks = ContentChunkBuilder.Build(
            registry,
            snapshot.Rows,
            ContentChunkBuilder.EveryChunk(registry, snapshot.Rows),
            ContentPublishBaseline.Empty,
            ContentSideRowEncoder.Default,
            findings);

        if (findings.Count > 0)
        {
            throw new ContentAuthoringException(
                Describe(record.VersionNumber, findings),
                default,
                0,
                ContentAuthoringException.CandidateInvalidReason,
                findings);
        }

        string ruleHash = ContentRuleChunkCodec.Hash(snapshot.Rules);
        ContentManifest server = ContentManifestBuilder.Build(
            ContentManifestSide.Server, registry, chunks, snapshot.Languages, record.VersionNumber,
            record.MinimumServerBuild, record.MinimumClientBuild, ruleHash);
        ContentManifest client = ContentManifestBuilder.Build(
            ContentManifestSide.Client, registry, chunks, snapshot.Languages, record.VersionNumber,
            record.MinimumServerBuild, record.MinimumClientBuild, ruleHash);

        return new ContentRebuiltPack(chunks, ruleHash, server, client);
    }

    /// <summary>
    /// Step 9's order, against a store that may already hold some of it. The manifests go in under the
    /// RECORD's hashes rather than the rebuilt ones, which the verification has just proved are the same
    /// strings, so the address a boot will ask for is the address the bytes land at by construction.
    /// </summary>
    static async Task<(int Objects, long Bytes)> WriteAsync(
        IPackStore target,
        IPackVersionPointerStore pointers,
        ContentVersionRecord record,
        ContentRebuiltPack pack,
        IReadOnlyList<RemapRule> rules,
        CancellationToken cancellationToken)
    {
        int objects = 0;
        long bytes = 0;

        for (int i = 0; i < pack.Chunks.Count; i++)
        {
            ContentChunkRecord chunk = pack.Chunks[i];
            (int wrote, long put) = await ContentPublishCommit
                .PutIfAbsentAsync(target, chunk.Hash, chunk.StoredFile, cancellationToken).ConfigureAwait(false);
            objects += wrote;
            bytes += put;
        }

        (int ruleWrote, long ruleBytes) = await ContentPublishCommit.PutIfAbsentAsync(
            target, pack.RuleChunkHash, ContentRuleChunkCodec.Encode(rules), cancellationToken)
            .ConfigureAwait(false);
        (int serverWrote, long serverBytes) = await ContentPublishCommit.PutIfAbsentAsync(
            target, record.ServerManifestHash, ContentManifestCodec.Encode(pack.Server), cancellationToken)
            .ConfigureAwait(false);
        (int clientWrote, long clientBytes) = await ContentPublishCommit.PutIfAbsentAsync(
            target, record.ClientManifestHash, ContentManifestCodec.Encode(pack.Client), cancellationToken)
            .ConfigureAwait(false);

        objects += ruleWrote + serverWrote + clientWrote;
        bytes += ruleBytes + serverBytes + clientBytes;

        await pointers.PutVersionPointerAsync(
            record.VersionNumber, record.ServerManifestHash, record.ClientManifestHash, cancellationToken)
            .ConfigureAwait(false);

        return (objects, bytes);
    }

    static string Describe(int versionNumber, IReadOnlyList<ContentFinding> findings)
    {
        var builder = new StringBuilder();
        builder.Append("The rows of version ")
            .Append(versionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(" do not encode, so nothing was written:");
        for (int i = 0; i < findings.Count; i++)
        {
            ContentFinding finding = findings[i];
            builder.Append(i > 0 ? " | " : " ").Append(finding.Code).Append(' ').Append(finding.Message);
        }

        return builder.ToString();
    }

    /// <summary>
    /// What the build produced: the chunk rows, the rule chunk's address, and the two manifests the
    /// verification digests. It exists so the build is ONE call with one catch around it.
    /// </summary>
    sealed class ContentRebuiltPack(
        IReadOnlyList<ContentChunkRecord> chunks,
        string ruleChunkHash,
        ContentManifest server,
        ContentManifest client)
    {
        /// <summary>Every chunk row the rebuild encoded, at every side.</summary>
        public IReadOnlyList<ContentChunkRecord> Chunks { get; } = chunks;

        /// <summary>The rule chunk's content address.</summary>
        public string RuleChunkHash { get; } = ruleChunkHash;

        /// <summary>The rebuilt server manifest.</summary>
        public ContentManifest Server { get; } = server;

        /// <summary>The rebuilt client manifest.</summary>
        public ContentManifest Client { get; } = client;
    }
}
