using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>One attempt at fetching and verifying a manifest.</summary>
/// <param name="Success">True when the manifest decoded AND digested to the name it was fetched under.</param>
/// <param name="Hash">The content address the fetch used.</param>
/// <param name="Manifest">The manifest, or null on any refusal.</param>
/// <param name="Reason">The stable reason token, or null on success.</param>
public sealed record ContentManifestRead(bool Success, string Hash, ContentManifest? Manifest, string? Reason);

/// <summary>One attempt at fetching, verifying and decoding a single chunk.</summary>
/// <param name="Success">True when the chunk verified, decoded, and matched what the manifest says about it.</param>
/// <param name="Hash">The content address the fetch used.</param>
/// <param name="Chunk">The decoded chunk, or null on any refusal.</param>
/// <param name="Reason">The stable reason token, or null on success.</param>
public sealed record ContentChunkRead(bool Success, string Hash, ContentChunk? Chunk, string? Reason);

/// <summary>One lazy row lookup, which fetches at most the one chunk whose slots cover the id.</summary>
/// <param name="Success">True when the version carries a live or retired row under that id.</param>
/// <param name="Row">The row, or null on a miss or a refusal.</param>
/// <param name="Reason">The stable reason token, or null on success.</param>
public sealed record ContentRowRead(bool Success, ContentRow? Row, string? Reason);

/// <summary>One whole-pack read, which is the server's eager path at boot.</summary>
/// <param name="Success">True when every chunk the manifest names was fetched and verified.</param>
/// <param name="Snapshot">The assembled snapshot, or null on a refusal.</param>
/// <param name="Hash">The address the refusal happened at, or the manifest's own on success.</param>
/// <param name="Reason">The stable reason token, or null on success.</param>
public sealed record ContentPackRead(bool Success, ContentSnapshot? Snapshot, string? Hash, string? Reason);

/// <summary>
/// The ONE reader of spec 9.3, over one version's manifest: it verifies, decompresses and decodes a chunk,
/// and it assembles a <see cref="ContentSnapshot"/> out of the chunks that were asked for.
/// <para>
/// <b>The server and the client share it and differ only in when they call it.</b> A server calls
/// <see cref="ReadAllAsync"/> once at boot, because the validator runs on the full snapshot and because a
/// server that decoded lazily would pay a first-touch cost inside a tick. A client calls
/// <see cref="ReadRowAsync"/>, which touches at most the one chunk whose slots cover the id, which is what
/// makes a cold start a download budget rather than a decode budget. One decoder, one set of reason tokens.
/// </para>
/// <para>
/// <b>Verify comes before decode, always.</b> A chunk whose bytes do not hash to the name it was fetched
/// under is refused and NEVER used, because the content address is the entire integrity chain (spec 13.2)
/// and a reader that decoded first would already have acted on bytes nothing signed. The second refusal is
/// the manifest's: a chunk the manifest never named, or named at a different size, is refused too, because
/// accepting it would mean serving rows no manifest hash covers.
/// </para>
/// <para>
/// Nothing here throws on bytes a store handed it. Every refusal is false plus a stable token, because the
/// bytes may have come from a remote peer. The reasons this type adds to the decoders' own are FETCH
/// outcomes rather than decode outcomes, so they are outside the fixed decode list of spec 15.2 by design.
/// </para>
/// </summary>
public sealed class ContentPackReader
{
    /// <summary>The bytes do not digest to the content address they were fetched or filed under.</summary>
    public const string ReasonHashMismatch = "hash-mismatch";

    /// <summary>A manifest file decoded, and its canonical text does not digest to the name it was fetched under.</summary>
    public const string ReasonManifestHashMismatch = "manifest-hash-mismatch";

    /// <summary>The store has no object at that address, which is a transfer outcome rather than a decode one.</summary>
    public const string ReasonFetchFailed = "chunk-fetch-failed";

    /// <summary>
    /// The manifest names a type this build does not register, so there is no codec to decode its rows with.
    /// Boot refuses on the manifest's type list before a chunk is fetched at all (spec 9.6), and this is the
    /// same refusal seen by a caller that reached a chunk anyway.
    /// </summary>
    public const string ReasonTypeUnregistered = "chunk-type-unregistered";

    /// <summary>
    /// The chunks fetched, verified and decoded but NOT yet handed to a snapshot. <see cref="BuildSnapshot"/>
    /// empties it, because the snapshot copies every row body into its own per-type blob and holding both is
    /// paying for the catalog twice.
    /// <para>
    /// That bounds the EAGER path, which is the one that reads every chunk the manifest names: boot's peak is
    /// the decoded chunks OR the snapshot rather than both. It does not bound the lazy client path, where a
    /// reader that never builds a snapshot accumulates a chunk per slot range touched and frees none. That is
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/902 and it needs an eviction policy rather than a
    /// hand-off point.
    /// </para>
    /// </summary>
    readonly Dictionary<string, LoadedChunk> _chunks = new(StringComparer.Ordinal);
    RemapRuleSet? _rules;

    /// <summary>Opens a reader over one version.</summary>
    /// <param name="store">Where the objects are fetched from.</param>
    /// <param name="registry">The local registry, which is what supplies the row codecs.</param>
    /// <param name="manifest">The version's manifest, already fetched and verified.</param>
    /// <param name="manifestHash">The manifest's own content address, which becomes the snapshot's identity.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public ContentPackReader(
        IPackStore store,
        ContentTypeRegistry registry,
        ContentManifest manifest,
        string manifestHash)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifestHash);

        Store = store;
        Registry = registry;
        Manifest = manifest;
        ManifestHash = manifestHash;
    }

    /// <summary>The store every fetch goes to.</summary>
    public IPackStore Store { get; }

    /// <summary>The registry the row codecs come from.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>The version's manifest, which is the only authority on what this version contains.</summary>
    public ContentManifest Manifest { get; }

    /// <summary>The manifest's content address, which the assembled snapshot carries as its identity.</summary>
    public string ManifestHash { get; }

    /// <summary>How many distinct chunks have been fetched, verified and decoded so far.</summary>
    public int ChunksRead => _chunks.Count;

    /// <summary>
    /// Verifies a stored file against the content address it was fetched, or is being filed, under. It
    /// dispatches on the four-character magic, so a caller hashes an object the way the publisher did rather
    /// than guessing, and a file whose magic names no known kind is refused before any length field is read.
    /// <para>
    /// A chunk, a rule chunk and a text chunk are each digested over their CANONICAL uncompressed bytes, so
    /// the check costs a decompression. A manifest is digested over its canonical TEXT after decode, which is
    /// why it is the one kind that cannot be answered from the file bytes alone.
    /// </para>
    /// <para>
    /// An unknown magic answers <see cref="ReasonHashMismatch"/> rather than a token of its own, because that
    /// IS the finding: bytes that cannot be hashed under any known rule cannot be the object the address
    /// names.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="expectedHash"/> is null.</exception>
    public static bool TryVerify(ReadOnlySpan<byte> file, string expectedHash, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);

        if (file.Length < ContentPackFormat.MagicBytes)
        {
            reason = ReasonHashMismatch;
            return false;
        }

        ReadOnlySpan<byte> magic = file[..ContentPackFormat.MagicBytes];
        if (magic.SequenceEqual(ContentPackFormat.ChunkMagic))
        {
            return ContentChunkCodec.TryVerify(file, expectedHash, out reason);
        }

        if (magic.SequenceEqual(ContentPackFormat.RuleChunkMagic))
        {
            return ContentRuleChunkCodec.TryDecode(file, out RemapRuleSet? rules, out reason)
                && Matches(ContentRuleChunkCodec.Hash(rules.Rules), expectedHash, ReasonHashMismatch, out reason);
        }

        if (magic.SequenceEqual(ContentPackFormat.TextChunkMagic))
        {
            return ContentTextChunkCodec.TryDecode(file, out ContentTextChunk? text, out reason)
                && Matches(text.ComputeHash(), expectedHash, ReasonHashMismatch, out reason);
        }

        if (magic.SequenceEqual(ContentPackFormat.ManifestMagic))
        {
            ContentManifestSide side = file.Length > 6 && file[6] == (byte)ContentManifestSide.Client
                ? ContentManifestSide.Client
                : ContentManifestSide.Server;
            return ContentManifestCodec.TryDecode(file, side, out ContentManifest? manifest, out reason)
                && Matches(ContentManifestText.Hash(manifest), expectedHash, ReasonManifestHashMismatch, out reason);
        }

        reason = ReasonHashMismatch;
        return false;
    }

    /// <summary>
    /// Boot step 3: fetch one manifest by hash, decode it for the side the caller asked for, and check that
    /// its canonical text digests back to the name it was fetched under. An absent manifest, the other side's
    /// manifest and a manifest whose bytes were edited are three different reason tokens, because an operator
    /// reading one line has to be able to tell a store misconfiguration from a tampered file.
    /// </summary>
    /// <param name="store">Where the manifest is fetched from.</param>
    /// <param name="manifestHash">The content address the connect door, the pin or the pointer supplied.</param>
    /// <param name="side">Which side's manifest this is expected to be.</param>
    /// <param name="registry">The local registry, or null to skip the per-type slot-count cross-check.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="manifestHash"/> is null.</exception>
    public static async Task<ContentManifestRead> ReadManifestAsync(
        IPackStore store,
        string manifestHash,
        ContentManifestSide side,
        ContentTypeRegistry? registry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifestHash);

        ReadOnlyMemory<byte>? file = await store.GetAsync(manifestHash, cancellationToken).ConfigureAwait(false);
        return file is null
            ? new ContentManifestRead(false, manifestHash, null, ReasonFetchFailed)
            : DecodeManifest(manifestHash, side, registry, file.Value);
    }

    /// <summary>
    /// Fetches, verifies, decompresses and decodes ONE chunk, then decodes every row in it through its type's
    /// codec. A chunk already read is answered from memory and never refetched, which is what makes a second
    /// lookup inside the same slot range free.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="hash"/> is null.</exception>
    public async Task<ContentChunkRead> ReadChunkAsync(string hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        if (_chunks.TryGetValue(hash, out LoadedChunk? cached))
        {
            return new ContentChunkRead(true, hash, cached.Chunk, null);
        }

        ReadOnlyMemory<byte>? file = await Store.GetAsync(hash, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return new ContentChunkRead(false, hash, null, ReasonFetchFailed);
        }

        LoadedChunk? loaded = LoadChunk(hash, file.Value, out string? reason);
        if (loaded is null)
        {
            return new ContentChunkRead(false, hash, null, reason);
        }

        _chunks[hash] = loaded;
        return new ContentChunkRead(true, hash, loaded.Chunk, null);
    }

    /// <summary>
    /// The lazy path: read one row, fetching at most the ONE chunk whose slot range covers the id. An id
    /// outside every chunk this version carries is a MISS rather than a fetch, so a walk over an inventory
    /// holding a retired reference costs nothing over the wire.
    /// </summary>
    public async Task<ContentRowRead> ReadRowAsync(
        ContentTypeId type,
        int id,
        CancellationToken cancellationToken = default)
    {
        ManifestChunkEntry? entry = id < 0 ? null : FindChunkFor(type, id);
        if (entry is null)
        {
            return new ContentRowRead(false, null, ContentChunkCodec.ReasonRowMissing);
        }

        ContentChunkRead read = await ReadChunkAsync(entry.Hash, cancellationToken).ConfigureAwait(false);
        if (!read.Success)
        {
            return new ContentRowRead(false, null, read.Reason);
        }

        LoadedChunk loaded = _chunks[entry.Hash];
        return loaded.Chunk.TryGetIndex(id, out int index)
            ? new ContentRowRead(true, loaded.Rows[index], null)
            : new ContentRowRead(false, null, ContentChunkCodec.ReasonRowMissing);
    }

    /// <summary>
    /// Boot step 6 and 7: fetch and verify EVERY chunk the manifest names, then assemble the snapshot. The
    /// per-language text chunks are fetched and verified too, because the manifest names them and a version
    /// that is half present is a version no boot should serve, even though nothing in the snapshot holds text.
    /// <para>
    /// It stops at the FIRST refusal and names the address it happened at, so the operator's one line says
    /// which object is wrong rather than how many are.
    /// </para>
    /// </summary>
    public async Task<ContentPackRead> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        for (int t = 0; t < Manifest.Types.Count; t++)
        {
            IReadOnlyList<ManifestChunkEntry> chunks = Manifest.Types[t].Chunks;
            for (int c = 0; c < chunks.Count; c++)
            {
                ContentChunkRead read = await ReadChunkAsync(chunks[c].Hash, cancellationToken).ConfigureAwait(false);
                if (!read.Success)
                {
                    return new ContentPackRead(false, null, read.Hash, read.Reason);
                }
            }
        }

        ContentPackRead rules = await ReadRuleChunkAsync(cancellationToken).ConfigureAwait(false);
        if (!rules.Success)
        {
            return rules;
        }

        for (int l = 0; l < Manifest.Languages.Count; l++)
        {
            string textHash = Manifest.Languages[l].TextHash;
            ReadOnlyMemory<byte>? text = await Store.GetAsync(textHash, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                return new ContentPackRead(false, null, textHash, ReasonFetchFailed);
            }

            if (!TryVerify(text.Value.Span, textHash, out string? reason))
            {
                return new ContentPackRead(false, null, textHash, reason);
            }
        }

        return new ContentPackRead(true, BuildSnapshot(), ManifestHash, null);
    }

    /// <summary>
    /// The snapshot over the chunks read SO FAR, rows ascending by id whatever order the chunks arrived in.
    /// A reader that has only been asked for one row therefore builds a snapshot holding one chunk's rows,
    /// which is the whole shape of the lazy client path.
    /// <para>
    /// <b>It HANDS the chunks over rather than sharing them.</b> The snapshot copies every row body into its
    /// own per-type blob, so a reader that kept its decoded chunks afterwards would hold the whole catalog
    /// twice for the rest of the process, and boot's peak is exactly where that is least affordable. The
    /// decoded chunks are therefore dropped here, and a second call with nothing read since builds an EMPTY
    /// snapshot rather than the same one.
    /// </para>
    /// <para>
    /// Reading rows through the READER after building is therefore not a supported mode: read them from the
    /// snapshot, which is the thing that owns them. A lazy caller that keeps looking rows up through
    /// <see cref="ReadRowAsync"/> simply does not build a snapshot in between, and one that does pays a
    /// refetch for the slot ranges it asks for again.
    /// </para>
    /// </summary>
    public ContentSnapshot BuildSnapshot()
    {
        var builder = new ContentSnapshotBuilder(Registry)
            .WithIdentity((int)Manifest.VersionNumber, ManifestHash);

        if (_rules is not null)
        {
            builder.WithRules(_rules.Rules);
        }

        // By type then chunk index, so the snapshot a given set of chunks produces does not depend on the
        // order the dictionary happens to hand them back in.
        var ordered = new List<LoadedChunk>(_chunks.Values);
        ordered.Sort(static (left, right) => left.Chunk.Type.Value == right.Chunk.Type.Value
            ? left.Chunk.ChunkIndex.CompareTo(right.Chunk.ChunkIndex)
            : left.Chunk.Type.Value.CompareTo(right.Chunk.Type.Value));

        for (int i = 0; i < ordered.Count; i++)
        {
            LoadedChunk loaded = ordered[i];
            for (int r = 0; r < loaded.Rows.Length; r++)
            {
                builder.AddRow(loaded.Rows[r], loaded.Bodies[r]);
            }
        }

        ContentSnapshot snapshot = builder.Build();

        // Build COPIED every body into the snapshot's own blob, so the decoded chunks are dead weight now.
        _chunks.Clear();
        return snapshot;
    }

    static ContentManifestRead DecodeManifest(
        string hash,
        ContentManifestSide side,
        ContentTypeRegistry? registry,
        ReadOnlyMemory<byte> file)
    {
        if (!ContentManifestCodec.TryDecode(file.Span, side, registry, out ContentManifest? manifest, out string? reason))
        {
            return new ContentManifestRead(false, hash, null, reason);
        }

        return string.Equals(ContentManifestText.Hash(manifest), hash, StringComparison.Ordinal)
            ? new ContentManifestRead(true, hash, manifest, null)
            : new ContentManifestRead(false, hash, null, ReasonManifestHashMismatch);
    }

    static bool Matches(string actual, string expected, string mismatchReason, out string? reason)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            reason = null;
            return true;
        }

        reason = mismatchReason;
        return false;
    }

    async Task<ContentPackRead> ReadRuleChunkAsync(CancellationToken cancellationToken)
    {
        string hash = Manifest.RemapRuleChunkHash;
        if (_rules is not null)
        {
            return new ContentPackRead(true, null, hash, null);
        }

        ReadOnlyMemory<byte>? file = await Store.GetAsync(hash, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return new ContentPackRead(false, null, hash, ReasonFetchFailed);
        }

        return DecodeRules(hash, file.Value);
    }

    ContentPackRead DecodeRules(string hash, ReadOnlyMemory<byte> file)
    {
        if (!TryVerify(file.Span, hash, out string? reason))
        {
            return new ContentPackRead(false, null, hash, reason);
        }

        if (!ContentRuleChunkCodec.TryDecode(file.Span, out RemapRuleSet? rules, out reason))
        {
            return new ContentPackRead(false, null, hash, reason);
        }

        _rules = rules;
        return new ContentPackRead(true, null, hash, null);
    }

    ManifestChunkEntry? FindChunkFor(ContentTypeId type, int id)
    {
        for (int t = 0; t < Manifest.Types.Count; t++)
        {
            ManifestTypeEntry entry = Manifest.Types[t];
            if (entry.TypeId != type.Value)
            {
                continue;
            }

            long chunkIndex = (long)id / entry.ChunkSlots;
            for (int c = 0; c < entry.Chunks.Count; c++)
            {
                if (entry.Chunks[c].ChunkIndex == chunkIndex)
                {
                    return entry.Chunks[c];
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// Verifies and decodes ONE chunk over a single decompression. It was a <c>TryVerify</c> then a
    /// <c>TryDecode</c> over the same span, which decompressed the stored bytes twice and allocated the
    /// uncompressed body twice, per chunk, per boot.
    /// </summary>
    LoadedChunk? LoadChunk(string hash, ReadOnlyMemory<byte> file, out string? reason)
    {
        if (!ContentChunkCodec.TryDecodeVerified(file.Span, Registry, hash, out ContentChunk? chunk, out reason))
        {
            return null;
        }

        if (!Manifest.TryMatchChunkHeader(chunk.Type, (uint)chunk.ChunkIndex, (uint)chunk.Body.Length, out reason))
        {
            return null;
        }

        if (!Registry.TryGet(chunk.Type, out ContentTypeRegistration? registration))
        {
            reason = ReasonTypeUnregistered;
            return null;
        }

        var rows = new ContentRow[chunk.RowCount];
        var bodies = new ReadOnlyMemory<byte>[chunk.RowCount];
        for (int i = 0; i < rows.Length; i++)
        {
            if (!chunk.TryDecodeRowAt(i, registration.Codec, out ContentRow? row, out reason))
            {
                return null;
            }

            rows[i] = row;

            // A SLICE of the chunk's body, not a copy of it: spec 9.2 asks for no growth and no copy beyond
            // the decompress, and the snapshot takes its own copy into one blob per type anyway.
            bodies[i] = chunk.RowBodyMemoryAt(i);
        }

        reason = null;
        return new LoadedChunk(chunk, rows, bodies);
    }

    /// <summary>One chunk that verified and decoded, with its rows already decoded through the type's codec.</summary>
    sealed class LoadedChunk(ContentChunk chunk, ContentRow[] rows, ReadOnlyMemory<byte>[] bodies)
    {
        public ContentChunk Chunk { get; } = chunk;

        public ContentRow[] Rows { get; } = rows;

        public ReadOnlyMemory<byte>[] Bodies { get; } = bodies;
    }
}
