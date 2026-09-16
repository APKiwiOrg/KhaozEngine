using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Tests.Catalog.Runtime;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

/// <summary>
/// The client fetch loop of spec 8.7, from the door refusal to every chunk verified, and the six failure
/// modes of spec 8.8.
/// <para>
/// The claim under test is not that the loop is fast. It is that a client never holds half a version: the
/// loop reports success only when every hash the manifest names verified, it stores BYTES and decodes
/// nothing, and it takes its base address from configuration rather than from the refusal that sent it.
/// </para>
/// </summary>
public class ContentFetchLoopTests
{
    static ContentFetchOptions Immediate(int clientBuild = 12, int attempts = 1) => new()
    {
        ClientBuild = clientBuild,
        Attempts = attempts,
        BackoffStep = TimeSpan.Zero,
    };

    // Step 1 and step 2 of the loop: refused at the door with the server's version and hash, then the
    // manifest that hash names. The fetch is of THAT version and of nothing else.
    [Fact]
    public async Task A_refusal_drives_one_fetch_of_the_version_it_named()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.True(result.Success);
        Assert.Equal(ContentFetchOutcome.Complete, result.Outcome);
        Assert.Null(result.Reason);
        Assert.NotNull(result.Manifest);
        Assert.Equal((uint)CatalogPack.VersionNumber, result.Manifest.VersionNumber);
        Assert.Equal(ContentManifestSide.Client, result.Manifest.Side);

        // Every hash the client manifest names, the manifest itself included, and not the server manifest.
        Assert.Equal(pack.EveryClientHash().Order(), remote.Fetched.Order());
        Assert.DoesNotContain(pack.ServerManifestHash, remote.Fetched);
    }

    // Step 2's other half: from the cache when the cache holds it. A client that was refused twice in a row
    // does not re-download the manifest it already verified.
    [Fact]
    public async Task A_manifest_the_cache_holds_is_never_asked_of_the_remote()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        local.PlantPack(pack);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.True(result.Success);
        Assert.Empty(remote.Fetched);
        Assert.Equal(0, result.Progress.ChunksFetched);
        Assert.Equal(pack.EveryClientHash().Count - 1, result.Progress.ChunksCached);
    }

    // Step 3, first half: a pack written by a newer format generation than this build reads. There is
    // nothing the client can do about it, so it stops and the player is asked to update, and it is not
    // refetched either, because a second copy of the same bytes cannot make this build newer.
    //
    // The refusal comes from the manifest DECODER rather than from a field comparison here: the codec
    // refuses a generation above its own before it builds a manifest at all, so the loop maps that token to
    // the outcome instead of re-checking a field it can never see set.
    [Fact]
    public async Task A_pack_from_a_newer_format_generation_stops_the_fetch_and_asks_the_player_to_update()
    {
        CatalogPack pack = CatalogPack.Build();
        ContentManifest ahead = pack.ClientManifest with { FormatGeneration = ContentPackFormat.Generation + 1 };
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        string hash = ContentManifestText.Hash(ahead);
        remote.Plant(hash, ContentManifestCodec.Encode(ahead));
        remote.PlantPack(pack, manifest: false);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(new ContentVersionIdentity(CatalogPack.VersionNumber, hash));

        Assert.False(result.Success);
        Assert.Equal(ContentFetchOutcome.GenerationTooNew, result.Outcome);
        Assert.Equal(ContentManifestCodec.ReasonFormatGeneration, result.Reason);
        Assert.Equal(hash, Assert.Single(remote.Fetched));
    }

    // Step 3, second half. The build ordinal is the consumer's own and the publisher named the floor, so the
    // refusal carries the build the player has to reach rather than a generic mismatch.
    [Fact]
    public async Task A_client_below_the_minimum_client_build_stops_the_fetch_and_asks_the_player_to_update()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate(clientBuild: 11));

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.False(result.Success);
        Assert.Equal(ContentFetchOutcome.ClientBuildTooOld, result.Outcome);
        Assert.Equal(ContentFetchLoop.ReasonClientBuildTooOld, result.Reason);
        Assert.Equal(12, result.MinimumClientBuild);
        Assert.Equal(pack.ClientManifestHash, Assert.Single(remote.Fetched));
    }

    // Step 4: `missing` is every chunk hash in the manifest not in the local cache, recomputed from the
    // cache itself rather than from any resume state.
    [Fact]
    public async Task The_missing_set_is_every_hash_the_manifest_names_that_the_cache_lacks()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        local.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.Span);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.True(result.Success);
        Assert.DoesNotContain(pack.TagChunk.Hash, remote.Fetched);
        Assert.Equal(1, result.Progress.ChunksCached);
        Assert.Equal(pack.EveryClientHash().Count - 2, result.Progress.ChunksFetched);
        Assert.True(result.Progress.BytesFetched > 0);
    }

    // Step 5's bound. Four, because a home connection saturates at two or three streams and unbounded
    // parallelism against a CDN buys nothing. The gate opens at four in flight, so a peak of exactly four
    // fails in both directions: a serial loop never opens it and an unbounded one overshoots it.
    [Fact]
    public async Task The_missing_set_is_fetched_at_bounded_concurrency_four()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        remote.GateAt(4);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.True(result.Success);
        Assert.Equal(4, remote.PeakConcurrency);
        Assert.Equal(4, loop.Options.Concurrency);
    }

    // Step 5's retry: ONCE, against the same source. A transfer that dropped bytes the second time is a
    // different roll of the same dice, and a third attempt against a source that has failed twice is the
    // backoff's job rather than this loop's.
    [Fact]
    public async Task A_chunk_that_fails_verification_is_refetched_once_against_the_same_source()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        remote.CorruptFirstGet(pack.ItemChunkOne.Hash);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.True(result.Success);
        Assert.Equal(2, remote.GetCount(pack.ItemChunkOne.Hash));
        Assert.True(await local.ExistsAsync(pack.ItemChunkOne.Hash));
    }

    // Spec 8.8 row 2, the half where the bytes ARRIVE short. They cannot be the object the address names,
    // so they are discarded and never cached, the fetch is retried once against the same source, and the
    // client stays at the door. The reported reason is the verify's OWN token rather than a token this loop
    // invents, because an operator reading one line has to be able to tell a short body from a wrong one.
    [Fact]
    public async Task A_truncated_chunk_is_discarded_retried_once_and_then_reported()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        remote.Plant(pack.ItemChunkOne.Hash, pack.ItemChunkOne.StoredFile.Span[..^4]);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.False(result.Success);
        Assert.Equal(ContentFetchOutcome.ChunksMissing, result.Outcome);
        Assert.Equal(ContentChunkCodec.ReasonStoredLength, result.Reason);
        Assert.Equal(pack.ItemChunkOne.Hash, result.Hash);
        Assert.Equal(2, remote.GetCount(pack.ItemChunkOne.Hash));
        Assert.False(await local.ExistsAsync(pack.ItemChunkOne.Hash));

        ContentFetchFailure failure = Assert.Single(result.Failures);
        Assert.Equal(pack.ItemChunkOne.Hash, failure.Hash);
        Assert.Equal(ContentChunkCodec.ReasonStoredLength, failure.Reason);
    }

    // The other half of spec 8.8 row 2, and the one a real transport delivers: a body that died mid flight
    // never arrives at all, because HttpPackStore answers NULL for a connection that dropped rather than
    // handing up what it managed to read. Nothing arrived, so the reason is the transfer's own token.
    [Fact]
    public async Task A_chunk_that_never_arrives_is_retried_once_and_then_reported_as_a_fetch_failure()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        remote.Withhold(pack.ItemChunkOne.Hash);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.False(result.Success);
        Assert.Equal(ContentFetchOutcome.ChunksMissing, result.Outcome);
        Assert.Equal(ContentPackReader.ReasonFetchFailed, result.Reason);
        Assert.Equal(pack.ItemChunkOne.Hash, result.Hash);
        Assert.Equal(2, remote.GetCount(pack.ItemChunkOne.Hash));
        Assert.Equal(pack.ItemChunkOne.Hash, Assert.Single(result.Failures).Hash);
    }

    // Spec 8.8 row 3: a valid hash over a malformed body. The hash covers the canonical form, so this is
    // never whole-file corruption: it means the PUBLISHER wrote a bad chunk, and the client refuses with the
    // DECODE reason and never caches it.
    //
    // The refetch happens anyway, because the loop cannot tell a transfer that mangled bytes from a
    // publisher that wrote them mangled: a decoder that refused before the digest could be compared has no
    // digest to compare. One retry against the same source is the price of not needing to know.
    [Fact]
    public async Task A_valid_hash_over_a_malformed_body_is_refused_with_the_decode_reason_and_never_cached()
    {
        KeyValuePair<string, string>[] descending =
        [
            new("item.rune_sword.name", "Rune sword"),
            new("item.bronze_sword.name", "Bronze sword"),
        ];
        byte[] file = ContentTextChunkCodec.Encode(CatalogPack.LanguageTag, descending);
        string hash = ContentTextChunkCodec.Hash(CatalogPack.LanguageTag, descending);
        CatalogPack pack = CatalogPack.Build(textFile: file, textHash: hash);
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.False(result.Success);
        Assert.Equal(ContentFetchOutcome.ChunksMissing, result.Outcome);
        Assert.Equal(ContentTextChunkCodec.ReasonEntryOrder, result.Reason);
        Assert.Equal(hash, result.Hash);
        Assert.Equal(2, remote.GetCount(hash));
        Assert.False(await local.ExistsAsync(hash));
    }

    // Spec 8.8 row 4 and spec 11 row 7: a cached chunk that went bad on disk is not in `missing`, because
    // the cache says it is there. It is caught on FIRST USE, through the same verifying pair the loop
    // fetched through, and the entry is deleted and refetched rather than poisoning the session.
    [Fact]
    public async Task A_cached_chunk_gone_bad_on_disk_is_detected_on_first_use_deleted_and_refetched()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        byte[] poisoned = pack.TagChunk.StoredFile.ToArray();
        poisoned[^1] ^= 0xFF;
        local.Plant(pack.TagChunk.Hash, poisoned);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.True(result.Success);
        Assert.DoesNotContain(pack.TagChunk.Hash, remote.Fetched);

        ReadOnlyMemory<byte>? bytes = await loop.Store.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(bytes);
        Assert.Equal(pack.TagChunk.StoredFile.ToArray(), bytes.Value.ToArray());
        Assert.Equal(1, local.Deletes);
        Assert.Equal(1, remote.GetCount(pack.TagChunk.Hash));
    }

    // Spec 8.8 row 5: the manifest is refetched ONCE and a second mismatch STOPS the client, because it
    // cannot tell a bad CDN from a bad configuration and guessing is worse than stopping. Nothing under the
    // manifest is fetched, since a manifest that did not verify names nothing this client may trust.
    [Fact]
    public async Task A_manifest_hash_mismatch_is_refetched_once_and_then_the_client_stops()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        // A manifest that decodes cleanly and is not the one that hash names, which is what a cache or a
        // proxy serving stale bytes for a content address looks like from here.
        remote.Plant(
            pack.ClientManifestHash,
            ContentManifestCodec.Encode(pack.ClientManifest with { MinimumServerBuild = 99 }));
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate(attempts: 3));

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.False(result.Success);
        Assert.Equal(ContentFetchOutcome.ManifestUnreadable, result.Outcome);
        Assert.Equal(ContentPackReader.ReasonManifestHashMismatch, result.Reason);
        Assert.Equal(pack.ClientManifestHash, result.Hash);
        Assert.Equal(2, remote.GetCount(pack.ClientManifestHash));
        Assert.Equal(2, remote.Fetched.Count);
        Assert.Null(result.Manifest);
    }

    // Spec 8.8 row 1 and spec 11 row 6: an interrupted fetch keeps what arrived and the next attempt
    // recomputes `missing` against the cache. There is no resume state anywhere, because a chunk is atomic.
    [Fact]
    public async Task An_interrupted_fetch_caches_what_arrived_and_the_next_attempt_fetches_only_the_rest()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        remote.Withhold(pack.ItemChunkOne.Hash);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());
        var version = new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash);

        ContentFetchResult first = await loop.FetchAsync(version);

        Assert.False(first.Success);
        Assert.Equal(ContentPackReader.ReasonFetchFailed, first.Reason);
        Assert.True(await local.ExistsAsync(pack.ItemChunkZero.Hash));

        remote.Serve(pack.ItemChunkOne.Hash);
        remote.Reset();

        ContentFetchResult second = await loop.FetchAsync(version);

        Assert.True(second.Success);
        Assert.Equal(pack.ItemChunkOne.Hash, Assert.Single(remote.Fetched));
    }

    // Spec 8.8 row 6: the server's version moving mid fetch needs no special casing at all. Chunk addresses
    // are content addresses, so the second fetch downloads the new manifest and the one chunk that differs.
    [Fact]
    public async Task The_servers_version_moving_downloads_only_what_differs()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        Assert.True((await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash))).Success);

        CatalogPack next = pack.WithEditedItemChunk();
        remote.PlantPack(next);
        remote.Reset();

        ContentFetchResult second = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber + 1, next.ClientManifestHash));

        Assert.True(second.Success);
        Assert.Equal(
            new[] { next.ClientManifestHash, next.ItemChunkOne.Hash }.Order(),
            remote.Fetched.Order());
    }

    // Decode is LAZY, spec 9.3: step 5 stored bytes and nothing else. A client that reads one item id
    // decompresses the one chunk whose slots cover it and leaves every other chunk compressed in the cache,
    // which is what makes the cold start a download budget rather than a decode budget.
    [Fact]
    public async Task A_client_that_reads_one_item_id_touches_one_chunk_and_leaves_the_rest_compressed()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        local.Reset();

        var reader = new ContentPackReader(loop.Store, pack.Registry, result.Manifest, pack.ClientManifestHash);
        ContentRowRead row = await reader.ReadRowAsync(CatalogPack.ItemType, 1030);

        Assert.True(row.Success);
        Assert.NotNull(row.Row);
        Assert.Equal("rune_sword", row.Row.Key.ToString());
        Assert.Equal(1, reader.ChunksRead);
        Assert.Equal(pack.ItemChunkOne.Hash, Assert.Single(local.Fetched));
    }

    // A partial download never becomes a partial catalog. The door comparison is a hash equality rather
    // than a negotiation exactly because there is no state in which a client holds half a version, so
    // nothing on this surface can hand a caller a snapshot, a runtime or a reader built from an incomplete
    // fetch: success is the only door out.
    [Fact]
    public async Task A_partial_download_never_becomes_a_partial_catalog()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        remote.Withhold(pack.ItemChunkOne.Hash);
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.False(result.Success);
        Assert.NotEqual(ContentFetchOutcome.Complete, result.Outcome);

        Type[] forbidden = [typeof(ContentSnapshot), typeof(IContentSnapshot), typeof(ContentPackReader), typeof(ContentRuntime)];
        foreach (Type surface in new[] { typeof(ContentFetchLoop), typeof(ContentFetchResult) })
        {
            foreach (MemberInfo member in surface.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                Type? produced = member switch
                {
                    PropertyInfo property => property.PropertyType,
                    MethodInfo method => method.ReturnType,
                    _ => null,
                };
                foreach (Type banned in forbidden)
                {
                    Assert.False(ReferenceEquals(banned, produced), surface.Name + "." + member.Name);
                }
            }
        }
    }

    // Spec 8.5: the client gets its base URL from CONFIGURATION, never from the refusal token. A URL in a
    // refusal token is a redirect an unauthenticated party controls. The base address is a constructor
    // argument, and the loop takes no string from a caller at all: the version it fetches arrives as a
    // parsed identity, so there is no code path in which a token's bytes become an address.
    [Fact]
    public void The_base_address_is_a_constructor_argument_and_nothing_parses_a_url_out_of_a_refusal()
    {
        ConstructorInfo[] constructors = typeof(ContentFetchLoop).GetConstructors();
        Assert.Contains(
            constructors,
            constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(Uri)));

        foreach (MethodInfo method in typeof(ContentFetchLoop).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.DeclaringType != typeof(ContentFetchLoop))
            {
                continue;
            }

            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(string));
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(Uri));
            Assert.NotEqual(typeof(Uri), method.ReturnType);
        }
    }

    // Step 6: report progress and retry the missing set with backoff. Every report is cumulative against
    // the same required set, so a client draws one bar rather than one per attempt.
    [Fact]
    public async Task Progress_is_reported_against_the_required_set_and_the_missing_set_is_retried()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        remote.WithholdUntilAttempt(pack.ItemChunkOne.Hash, 5);
        var reports = new List<ContentFetchProgress>();
        var options = new ContentFetchOptions
        {
            ClientBuild = 12,
            Attempts = 3,
            BackoffStep = TimeSpan.Zero,
            Progress = new Progressed(reports.Add),
        };
        var loop = new ContentFetchLoop(local, remote, pack.Registry, options);

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.True(result.Success);
        Assert.Equal(3, result.Attempts);
        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.Equal(pack.EveryClientHash().Count - 1, report.ChunksRequired));
        Assert.Equal(pack.EveryClientHash().Count - 1, reports[^1].ChunksHeld);
        Assert.Equal(1.0, reports[^1].Fraction);
    }

    /// <summary>A sink that forwards to a lambda, so the test does not depend on a progress implementation.</summary>
    sealed class Progressed(Action<ContentFetchProgress> report) : IProgress<ContentFetchProgress>
    {
        public void Report(ContentFetchProgress value) => report(value);
    }

    /// <summary>
    /// A thread-safe in-memory store that counts what was asked of it, because the loop asks in parallel and
    /// the facts worth pinning are which hashes were fetched, how many times, and how many at once.
    /// </summary>
    internal sealed class FetchPackStore : IPackStore, IPackStorePruning
    {
        readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<string, int> _gets = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<string, int> _withheldUntil = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<string, int> _corruptUntil = new(StringComparer.Ordinal);
        readonly ConcurrentQueue<string> _fetched = new();
        TaskCompletionSource? _gate;
        int _gateAt;
        int _inFlight;
        int _peak;
        int _deletes;

        /// <summary>Every hash <see cref="GetAsync"/> answered bytes for, in completion order.</summary>
        public IReadOnlyCollection<string> Fetched => _fetched;

        /// <summary>The most fetches this store ever had in flight at once.</summary>
        public int PeakConcurrency => Volatile.Read(ref _peak);

        /// <summary>How many entries were evicted, which is the poisoned-cache path.</summary>
        public int Deletes => Volatile.Read(ref _deletes);

        /// <summary>Files bytes under a name WITHOUT verifying they digest to it.</summary>
        public void Plant(string hash, ReadOnlySpan<byte> bytes) => _objects[hash] = bytes.ToArray();

        /// <summary>Files every client-side object of one published version.</summary>
        public void PlantPack(CatalogPack pack, bool manifest = true)
        {
            foreach (KeyValuePair<string, byte[]> entry in pack.ClientObjects())
            {
                if (manifest || !string.Equals(entry.Key, pack.ClientManifestHash, StringComparison.Ordinal))
                {
                    Plant(entry.Key, entry.Value);
                }
            }
        }

        /// <summary>Holds each fetch until <paramref name="count"/> are in flight, so the bound is observable.</summary>
        public void GateAt(int count)
        {
            _gateAt = count;
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Answers null for this hash, which is an object the source does not hold.</summary>
        public void Withhold(string hash) => _withheldUntil[hash] = int.MaxValue;

        /// <summary>Answers null for this hash until the given fetch of it, then serves it.</summary>
        public void WithholdUntilAttempt(string hash, int attempt) => _withheldUntil[hash] = attempt;

        /// <summary>Stops withholding a hash.</summary>
        public void Serve(string hash) => _withheldUntil.TryRemove(hash, out _);

        /// <summary>Answers bytes that do not digest to their name on the FIRST fetch of this hash only.</summary>
        public void CorruptFirstGet(string hash) => _corruptUntil[hash] = 1;

        /// <summary>Forgets what was asked, so a second fetch is counted on its own.</summary>
        public void Reset()
        {
            _fetched.Clear();
            _gets.Clear();
        }

        /// <summary>How many times this hash was asked for.</summary>
        public int GetCount(string hash) => _gets.TryGetValue(hash, out int count) ? count : 0;

        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(_objects.ContainsKey(hash) && !_withheldUntil.ContainsKey(hash));

        public async Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
        {
            int attempt = _gets.AddOrUpdate(hash, 1, static (_, count) => count + 1);
            int flight = Interlocked.Increment(ref _inFlight);
            Peak(flight);
            try
            {
                await GateAsync(flight, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }

            if (_withheldUntil.TryGetValue(hash, out int until) && attempt < until)
            {
                return null;
            }

            if (!_objects.TryGetValue(hash, out byte[]? bytes))
            {
                return null;
            }

            if (_corruptUntil.TryGetValue(hash, out int corruptUntil) && attempt <= corruptUntil)
            {
                byte[] corrupt = bytes.ToArray();
                corrupt[^1] ^= 0xFF;
                bytes = corrupt;
            }

            _fetched.Enqueue(hash);
            return bytes;
        }

        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            _objects[hash] = bytes.ToArray();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ListAsync(
            int versionNumber,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public async IAsyncEnumerable<string> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            foreach (string hash in _objects.Keys)
            {
                yield return hash;
            }
        }

        public Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _deletes);
            return Task.FromResult(_objects.TryRemove(hash, out _));
        }

        async Task GateAsync(int flight, CancellationToken cancellationToken)
        {
            if (_gate is not TaskCompletionSource gate)
            {
                return;
            }

            if (flight >= _gateAt)
            {
                gate.TrySetResult();
            }

            // The timeout is a FAILURE path, not a delay: a loop that never reaches the bound falls through
            // it with a peak below four rather than hanging the suite.
            await Task.WhenAny(gate.Task, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken))
                .ConfigureAwait(false);
        }

        void Peak(int flight)
        {
            int seen = Volatile.Read(ref _peak);
            while (flight > seen)
            {
                int previous = Interlocked.CompareExchange(ref _peak, flight, seen);
                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }
}
