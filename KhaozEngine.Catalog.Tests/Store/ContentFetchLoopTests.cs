using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Primitives;
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
        BackoffBase = TimeSpan.Zero,
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
            BackoffBase = TimeSpan.Zero,
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

    // The manifest carries the build ordinal as a UINT and the client surface is an INT, so a version naming
    // a floor above int.MaxValue used to hand back a NEGATIVE build, and ContentRefusal.ClientTooOld throws
    // on a negative. Saturating fails closed instead: no client build can reach int.MaxValue, so the player
    // is told to update, which is the right answer for a floor no build of this head can meet.
    [Fact]
    public async Task A_minimum_client_build_above_int_MaxValue_saturates_rather_than_going_negative()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        ContentManifest raised = pack.ClientManifest with { MinimumClientBuild = uint.MaxValue };
        string hash = ContentManifestText.Hash(raised);
        remote.Plant(hash, ContentManifestCodec.Encode(raised));
        var loop = new ContentFetchLoop(local, remote, pack.Registry, Immediate());

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, hash));

        Assert.False(result.Success);
        Assert.Equal(ContentFetchOutcome.ClientBuildTooOld, result.Outcome);
        Assert.Equal(int.MaxValue, result.MinimumClientBuild);
    }

    // Spec 13.4: exponential backoff on a failed set, from 1 s, capped at 60 s, with FULL jitter. The jitter
    // is the point rather than a refinement: without a draw, every client of a restarted world retries on one
    // schedule and the backoff turns a thundering herd into a synchronized one. The delay is a SEAM, so this
    // asserts on eight waits without sleeping for any of them, and the source is seeded, so "never exactly at
    // its window" is a fact about this run rather than a probability.
    [Fact]
    public async Task The_backoff_is_exponential_capped_at_a_minute_and_fully_jittered()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        var local = new FetchPackStore();
        remote.PlantPack(pack);
        remote.Withhold(pack.ItemChunkOne.Hash);
        var waits = new List<TimeSpan>();
        var options = new ContentFetchOptions
        {
            ClientBuild = 12,
            Attempts = 9,
            Random = new SeededRandomSource(20260916),
            Delay = (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
        };
        var loop = new ContentFetchLoop(local, remote, pack.Registry, options);

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash));

        Assert.False(result.Success);
        Assert.Equal(9, result.Attempts);
        Assert.Equal(8, waits.Count);

        int atTheWindow = 0;
        for (int n = 1; n <= waits.Count; n++)
        {
            TimeSpan window = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, n - 1)));
            Assert.InRange(waits[n - 1], TimeSpan.Zero, window);
            if (waits[n - 1] == window)
            {
                atTheWindow++;
            }
        }

        Assert.Equal(0, atTheWindow);
        Assert.True(waits.Distinct().Count() > 1, "every wait was the same length");
    }

    // Cancellation is the ONE throwing exit on this surface, and the cache is what makes it safe: a chunk is
    // written to a temporary name and moved, so a fetch cancelled mid write keeps every chunk that completed,
    // leaves no half file behind, and the next call recomputes the missing set against what survived. There
    // is no resume state to corrupt, because a chunk is atomic.
    [Fact]
    public async Task A_cancelled_fetch_keeps_what_completed_leaves_no_temporary_and_the_next_call_finishes()
    {
        using var root = new TemporaryRoot();
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        remote.PlantPack(pack);
        var local = new FileSystemPackStore(root.Path);
        using var cancel = new CancellationTokenSource();
        var cancelling = new CancelAtStore(remote, 3, cancel);
        var loop = new ContentFetchLoop(local, cancelling, pack.Registry, new ContentFetchOptions
        {
            ClientBuild = 12,
            Attempts = 1,
            Concurrency = 1,
            BackoffBase = TimeSpan.Zero,
        });
        var version = new ContentVersionIdentity(CatalogPack.VersionNumber, pack.ClientManifestHash);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.FetchAsync(version, cancel.Token));

        var held = new List<string>();
        await foreach (string hash in local.EnumerateAsync())
        {
            held.Add(hash);
        }

        Assert.Equal(3, cancelling.Completed.Count);
        Assert.Equal(cancelling.Completed.Order(), held.Order());
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));

        cancelling.ServeEverything();
        ContentFetchResult second = await loop.FetchAsync(version);

        Assert.True(second.Success);
        Assert.Equal(pack.EveryClientHash().Count - 1, second.Progress.ChunksHeld);
    }
}
