using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>
/// The client fetch loop of spec 8.7, from the connect door's refusal to every chunk verified: read the
/// manifest the refusal named, check the two build gates, compute the missing set against the local cache,
/// fetch it at bounded concurrency, and report whether the client may reconnect.
/// <para>
/// <b>Decode is LAZY.</b> The loop stores BYTES. Nothing is decompressed for its own sake and no row is
/// decoded, which is what makes a cold start a download budget rather than a decode budget (spec 9.3,
/// budget P4). The one decompression a chunk pays here is the VERIFY's, inside the store pair, and its
/// result is dropped: a client that later reads one item id decompresses the one chunk whose slots cover it
/// and leaves every other chunk compressed in the cache.
/// </para>
/// <para>
/// <b>A partial download never becomes a partial catalog.</b> There is no member here that hands back a
/// snapshot, a runtime or a reader, and the client does not reconnect until every chunk in the manifest
/// verifies. That is what makes the door comparison a hash equality rather than a negotiation.
/// </para>
/// <para>
/// <b>The base address comes from CONFIGURATION, never from the refusal.</b> A URL in a refusal token is a
/// redirect an unauthenticated party controls (spec 8.5, 13.4), so the loop takes its store pair, or its
/// base address, as a constructor argument, and takes the version to fetch as a parsed
/// <see cref="ContentVersionIdentity"/> rather than as a token it would have to split. Nothing on this type
/// turns a string into an address.
/// </para>
/// </summary>
public sealed class ContentFetchLoop
{
    /// <summary>This build is below the version's <c>MinimumClientBuild</c>, so the player has to update.</summary>
    public const string ReasonClientBuildTooOld = "client-build-too-old";

    /// <summary>The fetch and the ONE retry against the same source that spec 8.7 step 5 allows a chunk.</summary>
    const int AttemptsPerObject = 2;

    readonly IPackStore local;
    readonly ConcurrentDictionary<string, string> refusals = new(StringComparer.Ordinal);

    /// <summary>Puts a verifying cache in front of a remote source and fetches through the pair.</summary>
    /// <param name="localCache">The client's own cache, which is the only half ever written.</param>
    /// <param name="remote">The origin: a static host or CDN through <see cref="HttpPackStore"/> (spec 8.6).</param>
    /// <param name="registry">This build's content type registry, which is what binds a manifest's types to codecs.</param>
    /// <param name="options">The consumer's own knobs, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ContentFetchOptions.Concurrency"/> or <see cref="ContentFetchOptions.Attempts"/> is not positive.</exception>
    public ContentFetchLoop(
        IPackStore localCache,
        IPackStore remote,
        ContentTypeRegistry registry,
        ContentFetchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(localCache);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(registry);

        Options = options ?? new ContentFetchOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Options.Concurrency, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Options.Attempts, nameof(options));

        local = localCache;
        Registry = registry;

        // The decorator is built HERE rather than handed in so its refusal callback is this loop's: a store
        // reports (hash, reason) without knowing what a fetch loop is, and this is the caller that turns
        // those reports into the reason a client shows at the door.
        Store = new CachingPackStore(localCache, remote, Refused);
    }

    /// <summary>
    /// The same loop over an HTTP container, which is the deployment spec 8.6 recommends. The base address
    /// is a CONSTRUCTOR argument, from the same configuration that carries the server address, and never
    /// from a refusal token.
    /// </summary>
    /// <param name="localCache">The client's own cache.</param>
    /// <param name="remoteClient">The client every request goes through, which the CALLER owns and disposes.</param>
    /// <param name="baseAddress">The container the shard tree hangs off.</param>
    /// <param name="registry">This build's content type registry.</param>
    /// <param name="options">The consumer's own knobs, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    /// <exception cref="ArgumentException">There is no absolute base address on either the client or the argument.</exception>
    public ContentFetchLoop(
        IPackStore localCache,
        HttpClient remoteClient,
        Uri baseAddress,
        ContentTypeRegistry registry,
        ContentFetchOptions? options = null)
        : this(localCache, new HttpPackStore(remoteClient, baseAddress), registry, options)
    {
    }

    /// <summary>
    /// The verifying pair every fetch went through, which is also what a lazy row read should go through
    /// afterwards: it verifies on every READ from the cache, so a cached chunk that went bad on disk is
    /// detected on first use, evicted and refetched (spec 8.8 row 4, spec 11 row 7).
    /// </summary>
    public CachingPackStore Store { get; }

    /// <summary>The registry a manifest's types are bound to.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>The knobs this loop was built with.</summary>
    public ContentFetchOptions Options { get; }

    /// <summary>
    /// Runs the loop for ONE version: the one the connect door refused with. The identity arrives already
    /// parsed, so nothing here reads a wire token.
    /// <para>
    /// It is callable again after any outcome, and the second call recomputes the missing set from the cache
    /// itself. There is no resume state anywhere: a chunk is atomic, so the chunks that arrived are simply
    /// chunks the cache now holds, and a server whose version moved mid fetch needs no special casing at
    /// all, because chunk addresses are content addresses.
    /// </para>
    /// </summary>
    /// <param name="version">The version the door named, number and manifest hash.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <exception cref="ArgumentException"><paramref name="version"/> carries no manifest hash.</exception>
    public async Task<ContentFetchResult> FetchAsync(
        ContentVersionIdentity version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(version.ManifestHash))
        {
            throw new ArgumentException(
                "A fetch needs the manifest hash the door refused with. A version number alone names no bytes.",
                nameof(version));
        }

        // Steps 1 to 3: the manifest the refusal named, then the two gates that mean the player has to
        // update rather than wait.
        ManifestOutcome manifest = await ReadManifestAsync(version, cancellationToken).ConfigureAwait(false);
        if (manifest.Manifest is null)
        {
            return Refuse(manifest.Outcome, version, null, version.ManifestHash, manifest.Reason);
        }

        if (Options.ClientBuild < manifest.Manifest.MinimumClientBuild)
        {
            return Refuse(
                ContentFetchOutcome.ClientBuildTooOld,
                version,
                manifest.Manifest,
                version.ManifestHash,
                ReasonClientBuildTooOld);
        }

        return await FetchChunksAsync(version, manifest.Manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every hash a manifest names, in manifest order: each type's chunks, the remap rule chunk, and the
    /// text chunk of every language this client wants. Its own address is NOT in it, because the manifest is
    /// what named the rest and is already held by the time this is asked.
    /// </summary>
    /// <param name="manifest">The version's manifest.</param>
    /// <param name="languages">The language tags to include, or empty for every language the version ships.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public static IReadOnlyList<string> RequiredHashes(
        ContentManifest manifest,
        IReadOnlyList<string>? languages = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var required = new List<string>(manifest.TotalChunkCount + manifest.Languages.Count + 1);
        for (int t = 0; t < manifest.Types.Count; t++)
        {
            IReadOnlyList<ManifestChunkEntry> chunks = manifest.Types[t].Chunks;
            for (int c = 0; c < chunks.Count; c++)
            {
                required.Add(chunks[c].Hash);
            }
        }

        required.Add(manifest.RemapRuleChunkHash);

        for (int l = 0; l < manifest.Languages.Count; l++)
        {
            ManifestLanguageEntry language = manifest.Languages[l];
            if (Wanted(languages, language.Tag))
            {
                required.Add(language.TextHash);
            }
        }

        return required;
    }

    static bool Wanted(IReadOnlyList<string>? languages, string tag)
    {
        if (languages is null || languages.Count == 0)
        {
            return true;
        }

        for (int i = 0; i < languages.Count; i++)
        {
            if (string.Equals(languages[i], tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Step 2, and step 3's first half. The manifest is fetched through the verifying pair, so it is cached
    /// only if it digested to the name the door handed over.
    /// <para>
    /// A mismatch is refetched ONCE and then the client STOPS: it cannot distinguish a bad CDN from a bad
    /// configuration and guessing is worse than stopping (spec 8.8 row 5). A pack from a newer format
    /// GENERATION is not retried at all, because the manifest decoder itself is what refuses it and a
    /// refetch cannot make this build newer.
    /// </para>
    /// </summary>
    async Task<ManifestOutcome> ReadManifestAsync(ContentVersionIdentity version, CancellationToken cancellationToken)
    {
        string? reason = null;
        for (int attempt = 0; attempt < AttemptsPerObject; attempt++)
        {
            ContentManifestRead read = await ContentPackReader
                .ReadManifestAsync(Store, version.ManifestHash, ContentManifestSide.Client, Registry, cancellationToken)
                .ConfigureAwait(false);
            if (read.Success && read.Manifest is not null)
            {
                refusals.TryRemove(version.ManifestHash, out _);
                return new ManifestOutcome(ContentFetchOutcome.Complete, read.Manifest, null);
            }

            // The store returns NULL for both an absent object and one it refused, so the decorator's own
            // report is what tells a missing manifest from a tampered one.
            reason = Take(version.ManifestHash) ?? read.Reason;
            if (string.Equals(reason, ContentManifestCodec.ReasonFormatGeneration, StringComparison.Ordinal))
            {
                return new ManifestOutcome(ContentFetchOutcome.GenerationTooNew, null, reason);
            }
        }

        return new ManifestOutcome(ContentFetchOutcome.ManifestUnreadable, null, reason);
    }

    /// <summary>
    /// Steps 4 to 6: the missing set against the cache, fetched at bounded concurrency, then reconnect or
    /// report and retry with backoff.
    /// </summary>
    async Task<ContentFetchResult> FetchChunksAsync(
        ContentVersionIdentity version,
        ContentManifest manifest,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> required = RequiredHashes(manifest, Options.Languages);
        var tally = new FetchTally(required.Count);
        int attempt = 0;
        IReadOnlyList<ContentFetchFailure> failures = [];

        while (true)
        {
            attempt++;
            List<string> missing = await MissingAsync(required, cancellationToken).ConfigureAwait(false);
            tally.StartAttempt(attempt, required.Count - missing.Count);

            await Parallel.ForEachAsync(
                    missing,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Options.Concurrency,
                        CancellationToken = cancellationToken,
                    },
                    (hash, token) => FetchOneAsync(hash, tally, token))
                .ConfigureAwait(false);

            failures = tally.Failures();
            if (failures.Count == 0)
            {
                return new ContentFetchResult(
                    ContentFetchOutcome.Complete,
                    version,
                    manifest,
                    null,
                    null,
                    attempt,
                    tally.Snapshot(attempt),
                    failures);
            }

            if (attempt >= Options.Attempts)
            {
                break;
            }

            // Report progress and retry the missing set with backoff. The set is recomputed from the cache
            // on the next pass, so everything that arrived this time is simply no longer missing.
            Options.Progress?.Report(tally.Snapshot(attempt));
            if (Options.BackoffBase > TimeSpan.Zero)
            {
                await Options.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        ContentFetchProgress progress = tally.Snapshot(attempt);
        Options.Progress?.Report(progress);
        return new ContentFetchResult(
            ContentFetchOutcome.ChunksMissing,
            version,
            manifest,
            failures[0].Hash,
            failures[0].Reason,
            attempt,
            progress,
            failures);
    }

    /// <summary>
    /// The wait before the next attempt over the missing set, spec 13.4: the base doubled once per attempt,
    /// capped, and then multiplied by a uniform draw in [0, 1].
    /// <para>
    /// FULL jitter, and the draw is the part that matters. A world restart refuses its whole population at
    /// once, so an exponential backoff with no draw moves that population together and arrives as one spike
    /// at every step of the curve. The draw is taken over TICKS rather than over a fraction, because the
    /// randomness seam hands out integers and a wait is an integer number of ticks anyway. Its modulo is
    /// biased by one part in 2^64 over a range of ticks, which is a rounding error on a timer.
    /// </para>
    /// </summary>
    TimeSpan Backoff(int attempt)
    {
        long ceiling = Math.Max(Options.BackoffCeiling.Ticks, 0);
        long window = Math.Min(Math.Max(Options.BackoffBase.Ticks, 0), ceiling);
        for (int doubled = 1; doubled < attempt && window < ceiling; doubled++)
        {
            window = window >= ceiling / 2 ? ceiling : window * 2;
        }

        return window <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)(Options.Random.NextULong() % (ulong)(window + 1)));
    }

    /// <summary>
    /// Step 4: every required hash the LOCAL cache lacks. Asked of the cache rather than of the pair,
    /// because asking the pair would answer yes for anything the origin holds, which is every hash there is.
    /// </summary>
    async Task<List<string>> MissingAsync(IReadOnlyList<string> required, CancellationToken cancellationToken)
    {
        var missing = new List<string>(required.Count);
        for (int i = 0; i < required.Count; i++)
        {
            if (!await local.ExistsAsync(required[i], cancellationToken).ConfigureAwait(false))
            {
                missing.Add(required[i]);
            }
        }

        return missing;
    }

    /// <summary>
    /// Step 5 for one object: fetch, verify, cache, and on a refusal retry ONCE against the same source
    /// before giving up on it.
    /// <para>
    /// The retry happens whatever the refusal was, because the loop cannot tell a transfer that mangled
    /// bytes from a publisher that wrote them mangled: a decoder that refused BEFORE the digest could be
    /// compared has no digest to compare. One retry against the same source is the price of not needing to
    /// know, and the REPORT keeps the refusal's own token either way, so the difference is still readable
    /// afterwards.
    /// </para>
    /// </summary>
    async ValueTask FetchOneAsync(string hash, FetchTally tally, CancellationToken cancellationToken)
    {
        string reason = ContentPackReader.ReasonFetchFailed;
        for (int attempt = 0; attempt < AttemptsPerObject; attempt++)
        {
            ReadOnlyMemory<byte>? bytes = await Store.GetAsync(hash, cancellationToken).ConfigureAwait(false);
            if (bytes is not null)
            {
                refusals.TryRemove(hash, out _);
                tally.Arrived(bytes.Value.Length);
                Options.Progress?.Report(tally.Snapshot());
                return;
            }

            reason = Take(hash) ?? ContentPackReader.ReasonFetchFailed;
        }

        tally.Failed(hash, reason);
        Options.Progress?.Report(tally.Snapshot());
    }

    ContentFetchResult Refuse(
        ContentFetchOutcome outcome,
        ContentVersionIdentity version,
        ContentManifest? manifest,
        string hash,
        string? reason)
    {
        var progress = new ContentFetchProgress(0, 0, 0, 0, 0, 0);
        Options.Progress?.Report(progress);
        return new ContentFetchResult(outcome, version, manifest, hash, reason, 0, progress, []);
    }

    void Refused(string hash, string reason) => refusals[hash] = reason;

    string? Take(string hash) => refusals.TryRemove(hash, out string? reason) ? reason : null;

    /// <summary>What reading the manifest produced: the manifest, or the outcome and reason that stopped it.</summary>
    readonly record struct ManifestOutcome(ContentFetchOutcome Outcome, ContentManifest? Manifest, string? Reason);

    /// <summary>
    /// The running counts, written from every worker. One required set for the whole call, so the fraction a
    /// client draws never moves backwards when an attempt retries.
    /// </summary>
    sealed class FetchTally(int required)
    {
        readonly ConcurrentQueue<ContentFetchFailure> failures = new();
        int cached;
        int fetched;
        int attempt;
        long bytes;

        /// <summary>Starts an attempt, taking the cached figure from the FIRST one only.</summary>
        public void StartAttempt(int number, int alreadyHeld)
        {
            attempt = number;
            if (number == 1)
            {
                cached = alreadyHeld;
            }

            failures.Clear();
        }

        public void Arrived(int length)
        {
            Interlocked.Increment(ref fetched);
            Interlocked.Add(ref bytes, length);
        }

        public void Failed(string hash, string reason) => failures.Enqueue(new ContentFetchFailure(hash, reason));

        /// <summary>What is still missing after the attempt that just ran, in the order the workers gave up.</summary>
        public IReadOnlyList<ContentFetchFailure> Failures() => [.. failures];

        public ContentFetchProgress Snapshot() => Snapshot(Volatile.Read(ref attempt));

        public ContentFetchProgress Snapshot(int number) => new(
            required,
            Volatile.Read(ref cached),
            Volatile.Read(ref fetched),
            failures.Count,
            Interlocked.Read(ref bytes),
            number);
    }
}
