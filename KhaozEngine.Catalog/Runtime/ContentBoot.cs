using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>
/// The server's content boot, spec 9.5, in the one order it runs in: resolve the version, fetch and verify
/// the manifest, refuse a pack this build cannot read, freeze the registry, fetch and verify every chunk,
/// decode, build the four engine indexes and then the registered ones, validate, publish, and resolve the
/// world's content keys.
/// <para>
/// <b>It fails CLOSED and it never exits the process.</b> Each of the twelve rows of spec 9.6's table comes
/// back as a <see cref="ContentBootResult"/> carrying exit code 3 and the operator's exact lines, and the
/// HOST writes them and exits. There is no fallback to code defaults anywhere on this path (contracts 10.5),
/// because a silent fallback catalog serves content no version names and an outage is at least noticed.
/// </para>
/// <para>
/// <b>Step 1 is the caller's.</b> Every content type is registered before this runs, because a registry is
/// built by the host out of the engine's own plus whatever the game declares, and the boot's own move is to
/// FREEZE it at step 6. Steps 12 and 13, the connect door and accepting connections, are the caller's too:
/// content loads before the world and both load before the door opens, so this returns with the runtime
/// published and the host builds its door over it.
/// </para>
/// </summary>
public static class ContentBoot
{
    /// <summary>Every line spec 9.6 writes opens with this, so an operator greps one token.</summary>
    public const string LinePrefix = "content: ";

    /// <summary>
    /// Runs the boot in spec 9.5's order and stops at the first refusal.
    /// </summary>
    /// <param name="options">Everything the boot needs, handed in rather than reached for.</param>
    /// <param name="cancellationToken">Cancels the fetches.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static async Task<ContentBootResult> RunAsync(
        ContentBootOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Step 2: the version, from exactly one place.
        int version = await ResolveVersionAsync(options, cancellationToken).ConfigureAwait(false);
        if (version <= 0)
        {
            return ContentBootResult.Refuse(
                ContentBootRefusal.NoActiveVersion,
                2,
                LinePrefix + "no active version. Publish one or set the pinned version.");
        }

        // Step 3: the pointer, then the manifest it names, then the number the manifest itself declares.
        ManifestStep step3 = await ReadManifestAsync(options, version, cancellationToken).ConfigureAwait(false);
        if (step3.Refusal is not null)
        {
            return step3.Refusal;
        }

        ContentBootResult rest = await ContinueAsync(options, step3, version, cancellationToken).ConfigureAwait(false);
        return step3.PointerCrossChecked ? rest.WithPackPointerCrossChecked() : rest;
    }

    /// <summary>Steps 4 to 11, once step 3 handed back a verified manifest.</summary>
    static async Task<ContentBootResult> ContinueAsync(
        ContentBootOptions options,
        ManifestStep step3,
        int version,
        CancellationToken cancellationToken)
    {
        ContentManifest manifest = step3.Manifest!;

        // Step 4 and step 5: a pack this build cannot read, and a build the pack will not be served by.
        // The ordinary path for step 4 is inside step 3, because ContentManifestCodec refuses a generation
        // above this build's before it finishes decoding. This is the same refusal for a manifest that
        // reached here another way, and it is kept rather than dropped so the step is checked where 9.5 puts
        // it even if the decoder ever stops checking.
        if (manifest.FormatGeneration > ContentPackFormat.Generation)
        {
            return GenerationRefusal(manifest.FormatGeneration);
        }

        if (options.ServerBuild < manifest.MinimumServerBuild)
        {
            return ContentBootResult.Refuse(
                ContentBootRefusal.ServerBuildTooOld,
                5,
                FormattableString.Invariant(
                    $"{LinePrefix}version {version} requires server build {manifest.MinimumServerBuild}. This build is {options.ServerBuild}."));
        }

        // Step 6: the type lists, both directions, before a single chunk is fetched.
        ContentBootResult? refusal = CheckTypes(options.Registry, manifest, version);
        if (refusal is not null)
        {
            return refusal;
        }

        return await LoadAsync(options, manifest, step3.Hash, version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Step 2's precedence, one order and no other (spec 10.7): a version pinned in the SERVER'S OWN CONFIG
    /// wins always, otherwise the authoring database's pinned version when it is not null, otherwise its
    /// active version. A server configured with a pin and a pack store therefore reads no authoring database
    /// at boot at all, which is the deployment this design recommends: the authoring database is a TOOLING
    /// dependency.
    /// <para>
    /// This is the version <see cref="RunAsync"/> will LOAD out of these same options, and 0 is the answer
    /// when nothing named one, which is step 2's refusal. It is public because a caller that PREPARES the
    /// pack store first has to prepare that version and no other: a <c>ContentPackRebuild</c> of the active
    /// version, run while a config pin or an operator's pin names a different one, fills the root with a pack
    /// the boot never asks for and the boot still refuses at step 3.
    /// </para>
    /// </summary>
    /// <param name="options">The same options the boot will be handed, since the answer is theirs.</param>
    /// <param name="cancellationToken">Cancels the directory reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static async Task<int> ResolveVersionAsync(
        ContentBootOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ConfiguredVersion is int configured)
        {
            return configured;
        }

        IContentVersionDirectory? directory = options.Directory;
        if (directory is null)
        {
            return 0;
        }

        int? pinned = await directory.GetPinnedVersionAsync(cancellationToken).ConfigureAwait(false);
        return pinned is int pin && pin > 0
            ? pin
            : await directory.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Step 3: the <c>versions/&lt;n&gt;</c> pointer, the cross-check that the pointer names the manifest the
    /// VERSION RECORD names, the server manifest it names, and the cross-check that the manifest's own
    /// embedded <c>versionNumber</c> is the version the boot resolved. Without that last one the server would
    /// announce one version number at the door while serving another version's chunks, and every client would
    /// compare hashes correctly against the wrong number.
    /// </summary>
    static async Task<ManifestStep> ReadManifestAsync(
        ContentBootOptions options,
        int version,
        CancellationToken cancellationToken)
    {
        IContentVersionPointerSource? pointers = options.Pointers ?? options.Store as IContentVersionPointerSource;
        PackVersionPointer? pointer = pointers is null
            ? null
            : await pointers.GetVersionPointerAsync(version, cancellationToken).ConfigureAwait(false);
        if (pointer is null)
        {
            // The object that is absent is the POINTER, which has no hash to name, so the line names the
            // version instead. Spec 9.6's table has no row of its own for it and this is the same refusal.
            return new ManifestStep(ContentBootResult.Refuse(
                ContentBootRefusal.ManifestUnreadable,
                3,
                FormattableString.Invariant(
                    $"{LinePrefix}manifest for version {version} absent from {options.StoreName}.")));
        }

        ContentBootPointerCheck.Outcome check = await ContentBootPointerCheck
            .RunAsync(options, version, pointer, cancellationToken)
            .ConfigureAwait(false);
        if (check.Refusal is not null)
        {
            return new ManifestStep(check.Refusal);
        }

        ContentManifestRead read = await ContentPackReader.ReadManifestAsync(
            options.Store,
            pointer.ServerManifestHash,
            ContentManifestSide.Server,
            options.Registry,
            cancellationToken).ConfigureAwait(false);
        if (!read.Success)
        {
            // Step 4's refusal is DETECTED here, because the manifest decoder checks the generation before
            // it finishes reading the record. The row of spec 9.6's table a refusal belongs to is decided by
            // what is wrong rather than by which layer saw it, so the generation is read back out of the
            // header (contracts 7.4 puts it at offset 12) and the boot writes step 4's line.
            if (string.Equals(read.Reason, ContentManifestCodec.ReasonFormatGeneration, StringComparison.Ordinal))
            {
                uint generation = await ReadGenerationAsync(options.Store, read.Hash, cancellationToken)
                    .ConfigureAwait(false);
                if (generation > ContentPackFormat.Generation)
                {
                    return new ManifestStep(GenerationRefusal(generation));
                }
            }

            return new ManifestStep(ContentBootResult.Refuse(
                ContentBootRefusal.ManifestUnreadable,
                3,
                FormattableString.Invariant(
                    $"{LinePrefix}manifest {read.Hash} {ManifestReason(read.Reason)} from {options.StoreName}.")));
        }

        ContentManifest manifest = read.Manifest!;
        if (manifest.VersionNumber != (uint)version)
        {
            return new ManifestStep(ContentBootResult.Refuse(
                ContentBootRefusal.ManifestVersionMismatch,
                3,
                FormattableString.Invariant(
                    $"{LinePrefix}manifest {read.Hash} declares version {manifest.VersionNumber}, expected {version}.")));
        }

        return new ManifestStep(manifest, read.Hash, check.CrossChecked);
    }

    /// <summary>Step 3's outcome: the verified manifest and the address it was fetched under, or a refusal.</summary>
    readonly struct ManifestStep
    {
        public ManifestStep(ContentBootResult refusal)
        {
            Refusal = refusal;
            Manifest = null;
            Hash = string.Empty;
            PointerCrossChecked = false;
        }

        public ManifestStep(ContentManifest manifest, string hash, bool pointerCrossChecked)
        {
            Refusal = null;
            Manifest = manifest;
            Hash = hash;
            PointerCrossChecked = pointerCrossChecked;
        }

        public ContentBootResult? Refusal { get; }

        public ContentManifest? Manifest { get; }

        public string Hash { get; }

        public bool PointerCrossChecked { get; }
    }

    /// <summary>Spec 9.6's row 4, wherever the generation was read.</summary>
    static ContentBootResult GenerationRefusal(uint generation)
        => ContentBootResult.Refuse(
            ContentBootRefusal.GenerationTooNew,
            4,
            FormattableString.Invariant(
                $"{LinePrefix}pack generation {generation} needs a newer server. This build reads {ContentPackFormat.Generation}."));

    /// <summary>
    /// The manifest header's <c>formatGeneration</c>, at offset 12, or 0 when the file cannot be read at
    /// all. It costs one extra fetch on a boot that is already refusing, and it buys the operator the
    /// number that says which engine build wrote the pack.
    /// </summary>
    static async Task<uint> ReadGenerationAsync(IPackStore store, string hash, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? file = await store.GetAsync(hash, cancellationToken).ConfigureAwait(false);
        return file is { Length: >= 16 } bytes ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span[12..]) : 0;
    }

    /// <summary>
    /// The two phrases spec 9.6's manifest row names, and the reader's own token for anything else. A store
    /// with nothing at the address and a store holding bytes that are not the object it names are different
    /// operator problems, so they read differently.
    /// </summary>
    static string ManifestReason(string? reason) => reason switch
    {
        ContentPackReader.ReasonFetchFailed => "absent",
        ContentPackReader.ReasonManifestHashMismatch or ContentPackReader.ReasonHashMismatch => "hash mismatch",
        null => "hash mismatch",
        _ => reason,
    };

    /// <summary>
    /// Step 6's two type-registration refusals, in both directions. A manifest naming a type this build does
    /// not register has no codec to decode its rows with. A registered type ABSENT from the version is the
    /// same failure from the other side, and the tempting answer, an empty runtime table, is worse: every
    /// reference into that type then resolves to nothing and the operator reads a page of <c>KEC0006</c>
    /// findings instead of one line naming the missing type.
    /// </summary>
    static ContentBootResult? CheckTypes(ContentTypeRegistry registry, ContentManifest manifest, int version)
    {
        IReadOnlyList<ManifestTypeEntry> named = manifest.Types;
        for (int i = 0; i < named.Count; i++)
        {
            ManifestTypeEntry entry = named[i];
            if (!registry.TryGet(new ContentTypeId(entry.TypeId), out ContentTypeRegistration? _))
            {
                return ContentBootResult.Refuse(
                    ContentBootRefusal.TypeUnregistered,
                    6,
                    FormattableString.Invariant(
                        $"{LinePrefix}version {version} names type {entry.TypeId} which this build does not register."));
            }
        }

        IReadOnlyList<ContentTypeRegistration> registered = registry.ByTypeId;
        for (int i = 0; i < registered.Count; i++)
        {
            ContentTypeRegistration registration = registered[i];
            if (!Names(named, registration.Type.Value))
            {
                return ContentBootResult.Refuse(
                    ContentBootRefusal.TypeAbsentFromVersion,
                    6,
                    FormattableString.Invariant(
                        $"{LinePrefix}type {registration.TypeKey} ({registration.Type.Value}) is registered and absent from version {version}."));
            }
        }

        return null;
    }

    static bool Names(IReadOnlyList<ManifestTypeEntry> types, ushort typeId)
    {
        for (int i = 0; i < types.Count; i++)
        {
            if (types[i].TypeId == typeId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Steps 6 to 11: freeze, fetch and verify every chunk, decode, index, validate, publish, and resolve the
    /// world's keys.
    /// </summary>
    static async Task<ContentBootResult> LoadAsync(
        ContentBootOptions options,
        ContentManifest manifest,
        string manifestHash,
        int version,
        CancellationToken cancellationToken)
    {
        // Step 6's second half. The freeze is BEFORE the fetch rather than after it, because the reader
        // fuses verify and decode into one pass and this is the last point that is still ahead of both. A
        // registration after the first pack load would be a registry saying something the loaded bytes do
        // not, and Freeze is idempotent, so a second boot in one process is not a refusal.
        options.Registry.Freeze();

        ContentPackReader reader;
        try
        {
            reader = new ContentPackReader(options.Store, options.Registry, manifest, manifestHash);
        }
        catch (ContentPackException failure)
        {
            return ContentBootResult.Refuse(
                ContentBootRefusal.ManifestUnreadable,
                3,
                FormattableString.Invariant(
                    $"{LinePrefix}manifest {manifestHash} {failure.Reason ?? "hash mismatch"} from {options.StoreName}."));
        }

        ContentPackRead pack = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!pack.Success)
        {
            bool verify = IsVerifyFailure(pack.Reason);
            return ContentBootResult.Refuse(
                verify ? ContentBootRefusal.ChunkUnreadable : ContentBootRefusal.ChunkDecodeFailed,
                verify ? 6 : 7,
                FormattableString.Invariant($"{LinePrefix}chunk {pack.Hash} {pack.Reason}."));
        }

        // Step 7 and 7b: the four engine indexes come with the runtime, then every registered load index in
        // type id order.
        ContentSnapshot snapshot = pack.Snapshot!;
        ContentRuntime runtime = ContentRuntime.FromSnapshot(snapshot, options.Registry);
        try
        {
            runtime.BuildLoadIndexes();
        }
        catch (ContentLoadIndexException failure)
        {
            return ContentBootResult.Refuse(
                ContentBootRefusal.LoadIndexFailed,
                7,
                FormattableString.Invariant(
                    $"{LinePrefix}load index for type {failure.TypeKey} ({failure.Type.Value}) failed: {(failure.InnerException ?? failure).Message}."));
        }

        // Step 8: the one validator, with previous null, which is what it is at every boot.
        ContentValidationReport report = ContentValidator.Validate(
            snapshot,
            previous: null,
            snapshot.Rules,
            options.Registry);
        if (!report.IsValid)
        {
            return ContentBootResult.Refuse(ContentBootRefusal.ValidatorFindings, 8, FindingLines(report, options.Registry));
        }

        // Step 9: one Volatile.Write of a whole instance, which is the entire swap.
        options.Holder.Publish(runtime);

        // Step 11. The world document is the host's to load at step 10, and this is the boot-time check that
        // every key it names resolves. Publish is step 9 and this is step 11, so a refusal here leaves the
        // runtime in the holder and the host exits on the code rather than opening the door.
        ContentBootResult? world = ResolveWorldKeys(options, runtime, version);
        return world ?? ContentBootResult.Ok(runtime);
    }

    /// <summary>
    /// Which side of the chunk row a refusal fell on: a transfer outcome, which is step 6's verify, or a
    /// decode outcome, which is step 7's. The two rows of spec 9.6 write the same line, so the step is what
    /// tells them apart without parsing the token.
    /// </summary>
    static bool IsVerifyFailure(string? reason) => reason is ContentPackReader.ReasonFetchFailed
        or ContentPackReader.ReasonHashMismatch
        or ContentPackReader.ReasonManifestHashMismatch
        or ContentManifest.ReasonChunkRangeMismatch;

    /// <summary>
    /// Step 8's lines: one per finding, then the count. <c>KEC0000</c> is skipped and is not counted, because
    /// it is informational rather than a failure and <c>previous</c> is null at EVERY boot, so counting it
    /// would put one line the operator cannot act on at the top of every refusal.
    /// </summary>
    static IReadOnlyList<string> FindingLines(ContentValidationReport report, ContentTypeRegistry registry)
    {
        var lines = new List<string>(report.Findings.Count + 1);
        for (int i = 0; i < report.Findings.Count; i++)
        {
            ContentFinding finding = report.Findings[i];
            if (string.Equals(finding.Code, ContentValidator.InformationalCode, StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(FormattableString.Invariant(
                $"{LinePrefix}{finding.Code} {TypeName(registry, finding.Type)}/{finding.Id} {finding.Message}"));
        }

        lines.Add(FormattableString.Invariant($"{LinePrefix}{lines.Count} findings, refusing to serve."));
        return lines;
    }

    /// <summary>The type's key, or its number when the finding names a type no registration covers.</summary>
    static string TypeName(ContentTypeRegistry registry, ContentTypeId type)
        => registry.TryGet(type, out ContentTypeRegistration? registration)
            ? registration.TypeKey
            : type.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Step 11 (spec 3.10): every place the world document names content resolves by KEY against the loaded
    /// version. A world archetype or marker tag naming a key that is absent, retired or of a type this build
    /// does not register is a boot failure naming the key and the world source, rather than a null reference
    /// inside a tick.
    /// </summary>
    static ContentBootResult? ResolveWorldKeys(ContentBootOptions options, ContentRuntime runtime, int version)
    {
        IReadOnlyList<ContentWorldKeyReference> keys = options.WorldKeys;
        for (int i = 0; i < keys.Count; i++)
        {
            ContentWorldKeyReference reference = keys[i];
            bool live = options.Registry.TryGetByKey(reference.TypeKey, out ContentTypeRegistration? registration)
                && runtime.TryGetId(registration.Type, new ContentKey(reference.ContentKey), out int id)
                && !runtime.IsRetired(registration.Type, id);
            if (!live)
            {
                return ContentBootResult.Refuse(
                    ContentBootRefusal.WorldKeyUnresolved,
                    11,
                    FormattableString.Invariant(
                        $"{LinePrefix}world {reference.Source} references {reference.TypeKey}.{reference.ContentKey} which is not live in version {version}."));
            }
        }

        return null;
    }
}
