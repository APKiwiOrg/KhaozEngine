using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Tests.Catalog.Store;
using KhaozEngine.Tests.Catalog.Validation;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The ordered boot of spec 9.5 and the twelve refusals of 9.6, one fact per row of the exit table, each
/// asserting exit code 3 and the exact line the operator reads.
/// <para>
/// Every one runs IN PROCESS against <see cref="BootHost"/>, which records the exit rather than calling
/// <c>Environment.Exit</c>, so the whole table runs in one assembly instead of one child process per row.
/// That is possible because the boot never exits: it builds the lines and the code and hands them back, and
/// the host decides what to do with a process it owns and the engine does not.
/// </para>
/// </summary>
public class ContentBootTests
{
    [Fact]
    public async Task Step_2_with_no_version_anywhere_refuses()
    {
        using BootPack pack = await BootPack.CreateAsync();
        var host = new BootHost();

        // No config pin and no authoring database at all, which is the server that was never told anything.
        ContentBootResult result = await host.RunAsync(pack.Options(configuredVersion: null));

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.NoActiveVersion, result.Refusal);
        Assert.Equal(2, result.Step);
        Assert.Equal("content: no active version. Publish one or set the pinned version.", host.Line);
        Assert.False(pack.Holder.IsLoaded);

        // And the same line for a database that is reachable and has published nothing, because the operator
        // does the same thing about both.
        var empty = new BootHost();
        var directory = new FakeVersionDirectory(pinned: null, active: 0);
        await empty.RunAsync(pack.Options(configuredVersion: null, directory: directory));

        Assert.Equal(3, empty.ExitCode);
        Assert.Equal("content: no active version. Publish one or set the pinned version.", empty.Line);
        Assert.Equal(1, directory.ActiveReads);
    }

    [Fact]
    public async Task Step_3_a_manifest_absent_or_hash_mismatched_refuses()
    {
        using BootPack pack = await BootPack.CreateAsync();
        byte[] file = ContentManifestCodec.Encode(pack.Manifest);
        await pack.PointAtAsync(BootPack.AbsentHash);

        var absent = new BootHost();
        ContentBootResult result = await absent.RunAsync(pack.Options());

        Assert.Equal(3, absent.ExitCode);
        Assert.Equal(ContentBootRefusal.ManifestUnreadable, result.Refusal);
        Assert.Equal(3, result.Step);
        Assert.Equal(
            "content: manifest " + BootPack.AbsentHash + " absent from the test store.",
            absent.Line);

        // The other half of the row: the file is THERE and is not the object that address names, which the
        // store cannot be made to do through PutAsync and an attacker or a hand copy can.
        pack.WriteUnverified(BootPack.OtherHash, file);
        await pack.PointAtAsync(BootPack.OtherHash);

        var mismatched = new BootHost();
        await mismatched.RunAsync(pack.Options());

        Assert.Equal(3, mismatched.ExitCode);
        Assert.Equal(
            "content: manifest " + BootPack.OtherHash + " hash mismatch from the test store.",
            mismatched.Line);
    }

    [Fact]
    public async Task Step_3_a_manifest_declaring_another_version_refuses()
    {
        using BootPack pack = await BootPack.CreateAsync();

        // The pointer says 7 and the pack says 9, which is a store misconfiguration or a hand-copied file.
        // Without the cross-check the server would announce one number at the door and serve another's chunks.
        string hash = await pack.RepointAsync(pack.Manifest with { VersionNumber = 9 });

        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options());

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.ManifestVersionMismatch, result.Refusal);
        Assert.Equal("content: manifest " + hash + " declares version 9, expected 7.", host.Line);
    }

    [Fact]
    public async Task Step_4_a_generation_this_build_cannot_read_refuses()
    {
        using BootPack pack = await BootPack.CreateAsync();
        await pack.RepointAsync(pack.Manifest with { FormatGeneration = ContentPackFormat.Generation + 1 });

        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options());

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.GenerationTooNew, result.Refusal);
        Assert.Equal(4, result.Step);
        Assert.Equal(
            FormattableString.Invariant(
                $"content: pack generation {ContentPackFormat.Generation + 1} needs a newer server. This build reads {ContentPackFormat.Generation}."),
            host.Line);
    }

    [Fact]
    public async Task Step_5_a_server_build_under_the_versions_minimum_refuses()
    {
        using BootPack pack = await BootPack.CreateAsync();

        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options(serverBuild: 5));

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.ServerBuildTooOld, result.Refusal);
        Assert.Equal(5, result.Step);
        Assert.Equal("content: version 7 requires server build 11. This build is 5.", host.Line);
    }

    [Fact]
    public async Task Step_6_a_chunk_that_is_absent_refuses()
    {
        using BootPack pack = await BootPack.CreateAsync();
        string hash = pack.Chunks[0].Hash;
        Assert.True(await pack.DeleteAsync(hash));

        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options());

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.ChunkUnreadable, result.Refusal);
        Assert.Equal(6, result.Step);
        Assert.Equal("content: chunk " + hash + " chunk-fetch-failed.", host.Line);
        Assert.False(pack.Holder.IsLoaded);

        // The other half of the row: the object is there and is not the one the manifest named. Verify comes
        // before decode always, because the content address is the entire integrity chain and a reader that
        // decoded first would already have acted on bytes nothing signed.
        using BootPack swapped = await BootPack.CreateAsync();
        swapped.WriteUnverified(swapped.Chunks[0].Hash, swapped.Chunks[1].StoredFile.Span);

        var mismatched = new BootHost();
        ContentBootResult second = await mismatched.RunAsync(swapped.Options());

        Assert.Equal(3, mismatched.ExitCode);
        Assert.Equal(ContentBootRefusal.ChunkUnreadable, second.Refusal);
        Assert.Equal(6, second.Step);
        Assert.Equal("content: chunk " + swapped.Chunks[0].Hash + " hash-mismatch.", mismatched.Line);
    }

    [Fact]
    public async Task Step_6_a_manifest_naming_an_unregistered_type_refuses_before_a_chunk_is_fetched()
    {
        using BootPack pack = await BootPack.CreateAsync();
        var types = new List<ManifestTypeEntry>(pack.Manifest.Types)
        {
            new(1024, "store", 256, ContentVisibility.Client, []),
        };
        await pack.RepointAsync(pack.Manifest with { Types = types });

        var store = new RecordingPackStore(pack.PackStore);
        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options(store: store));

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.TypeUnregistered, result.Refusal);
        Assert.Equal(6, result.Step);
        Assert.Equal("content: version 7 names type 1024 which this build does not register.", host.Line);

        // Before a single chunk is fetched, because the manifest's type list is enough to decide it: the one
        // fetch is the manifest itself.
        Assert.Equal(1, store.GetCount);
    }

    [Fact]
    public async Task Step_6_a_registered_type_absent_from_the_version_refuses_from_the_other_side()
    {
        using BootPack pack = await BootPack.CreateAsync();
        var types = new List<ManifestTypeEntry>();
        foreach (ManifestTypeEntry entry in pack.Manifest.Types)
        {
            if (entry.TypeId != EngineContentTypes.StatTypeId)
            {
                types.Add(entry);
            }
        }

        await pack.RepointAsync(pack.Manifest with { Types = types });

        var store = new RecordingPackStore(pack.PackStore);
        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options(store: store));

        // The tempting answer, an empty runtime table, is worse: every reference into the type then resolves
        // to nothing and the operator reads a page of reference findings instead of the one line naming it.
        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.TypeAbsentFromVersion, result.Refusal);
        Assert.Equal(6, result.Step);
        Assert.Equal("content: type stat (3) is registered and absent from version 7.", host.Line);
        Assert.Equal(1, store.GetCount);
    }

    [Fact]
    public async Task Step_7_a_chunk_that_verifies_and_will_not_decode_refuses()
    {
        var rows = new List<BootRow>(BootPack.CleanRows())
        {
            // A body that is not a row: the chunk still digests to its own name, so verify passes and the
            // refusal is the decode's, which is the only way the two steps are told apart.
            new(EngineContentTypes.TagTypeKey, ContentValidationFixtures.Tag(2, "wood"), [0xFF, 0xFF, 0xFF]),
        };

        using BootPack pack = await BootPack.CreateAsync(rows: rows);
        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options());

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.ChunkDecodeFailed, result.Refusal);
        Assert.Equal(7, result.Step);
        Assert.StartsWith("content: chunk ", host.Line, StringComparison.Ordinal);
        Assert.EndsWith(".", host.Line, StringComparison.Ordinal);
        Assert.False(pack.Holder.IsLoaded);
    }

    [Fact]
    public async Task Step_7b_a_registered_load_index_that_threw_refuses()
    {
        ContentTypeRegistry registry = ContentValidationFixtures.EngineRegistry();
        RegisterGameType(registry, new BootThrowingIndex(new ContentTypeId(1024)));

        using BootPack pack = await BootPack.CreateAsync(registry);
        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options());

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.LoadIndexFailed, result.Refusal);
        Assert.Equal(
            "content: load index for type store (1024) failed: the store rows disagree with the item rows.",
            host.Line);

        // Step 7b is before step 9, so nothing was published: a partial index answers plausible wrong numbers
        // on a tick and a refused boot does not.
        Assert.False(pack.Holder.IsLoaded);
    }

    [Fact]
    public async Task Step_8_validator_findings_are_one_line_each_and_then_the_count()
    {
        var rows = new List<BootRow>
        {
            new(EngineContentTypes.TagTypeKey, ContentValidationFixtures.Tag(1, "metal")),

            // Tag 99 is not in this version, which is KEC0008 on the item whose tag list names it.
            new(EngineContentTypes.ItemTypeKey, ContentValidationFixtures.Item(7, "sword", tagIds: [99])),
        };

        using BootPack pack = await BootPack.CreateAsync(rows: rows);
        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options());

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.ValidatorFindings, result.Refusal);
        Assert.Equal(8, result.Step);
        Assert.Equal(2, host.Lines.Count);
        Assert.StartsWith("content: KEC0008 item/7 ", host.Lines[0], StringComparison.Ordinal);
        Assert.Equal("content: 1 findings, refusing to serve.", host.Lines[1]);

        // KEC0000 is not a failure and is not counted: previous is null at EVERY boot, so counting it would
        // put one line the operator cannot act on at the top of every refusal.
        Assert.DoesNotContain(ContentValidator.InformationalCode, host.Lines[0], StringComparison.Ordinal);
        Assert.False(pack.Holder.IsLoaded);
    }

    [Fact]
    public async Task Step_11_a_world_key_that_is_not_live_refuses()
    {
        var rows = new List<BootRow>(BootPack.CleanRows())
        {
            new(EngineContentTypes.ItemTypeKey, ContentValidationFixtures.Item(8, "rusty", isRetired: true)),
        };

        using BootPack pack = await BootPack.CreateAsync(rows: rows);
        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options(
            worldKeys: [new ContentWorldKeyReference("world/overworld.ktw", "item", "bronze_sword")]));

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.WorldKeyUnresolved, result.Refusal);
        Assert.Equal(11, result.Step);
        Assert.Equal(
            "content: world world/overworld.ktw references item.bronze_sword which is not live in version 7.",
            host.Line);

        // A RETIRED row is not live either, which is the case a key lookup alone would let through.
        var retired = new BootHost();
        await retired.RunAsync(pack.Options(
            worldKeys: [new ContentWorldKeyReference("world/overworld.ktw", "item", "rusty")]));

        Assert.Equal(3, retired.ExitCode);
        Assert.Equal(
            "content: world world/overworld.ktw references item.rusty which is not live in version 7.",
            retired.Line);

        // Publish is step 9 and the world resolves at 11, so the runtime IS in the holder when this refuses.
        // The host exits on the code rather than serving, which is what fail closed means here.
        Assert.True(pack.Holder.IsLoaded);
    }

    [Fact]
    public async Task Every_refusal_carries_exit_code_3_and_a_boot_that_published_carries_0()
    {
        using BootPack pack = await BootPack.CreateAsync();

        // 3 is distinct from the 2 a consumer already returns for a bad config, so a supervisor script tells
        // a content failure from a config failure without parsing text.
        Assert.Equal(3, ContentBootResult.ContentFailureExitCode);
        Assert.Equal(3, ContentBootResult.Refuse(ContentBootRefusal.NoActiveVersion, 2, "content: x").ExitCode);

        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options());

        Assert.True(result.Success);
        Assert.Equal(0, host.ExitCode);
        Assert.Empty(host.Lines);
        Assert.Equal(ContentBootRefusal.None, result.Refusal);
    }

    [Fact]
    public async Task A_boot_freezes_the_registry_builds_every_index_and_publishes_the_runtime()
    {
        ContentTypeRegistry registry = ContentValidationFixtures.EngineRegistry();
        var index = new BootRecordingIndex(new ContentTypeId(1024));
        RegisterGameType(registry, index);

        using BootPack pack = await BootPack.CreateAsync(registry);
        Assert.False(registry.IsFrozen);

        var host = new BootHost();
        ContentBootResult result = await host.RunAsync(pack.Options());

        Assert.True(result.Success);
        Assert.NotNull(result.Runtime);

        // Step 6 froze it, so a registration after the first pack load throws rather than producing a runtime
        // whose registry says something the loaded bytes do not.
        Assert.True(registry.IsFrozen);

        // Step 9 published it, and the holder hands back that same instance.
        Assert.True(pack.Holder.TryGetCurrent(out ContentRuntime? current));
        Assert.Same(result.Runtime, current);
        Assert.Equal(BootPack.VersionNumber, current!.Identity.Number);
        Assert.Equal(pack.ManifestHash, current.Identity.ManifestHash);

        // Step 7, the four engine indexes: the key index, the tag index, the family index and the loot index.
        Assert.True(current.TryGetId(new ContentTypeId(EngineContentTypes.ItemTypeId), new ContentKey("sword"), out int id));
        Assert.Equal(7, id);
        Assert.Equal(
            new[] { 7 },
            current.Indexes.Tags.Ids(new ContentTypeId(EngineContentTypes.ItemTypeId), 1).ToArray());
        Assert.Empty(current.Indexes.Families.Blocks);
        Assert.Equal(1, current.Indexes.Loot.EntryCount(100));

        // Step 7b, in type id order, after the four and before the validator.
        Assert.True(current.LoadIndexesBuilt);
        Assert.Equal(1, index.BuildCount);
        Assert.Equal(1, index.ItemsSeen);
    }

    [Fact]
    public async Task Step_2_takes_the_config_pin_then_the_stores_pin_then_the_active_version()
    {
        using BootPack pack = await BootPack.CreateAsync();

        // The config pin wins ALWAYS, and the authoring database is not even read, which is the deployment
        // this design recommends: a server with a pin and a pack store needs no authoring database at boot.
        var ignored = new FakeVersionDirectory(pinned: 9, active: 9);
        Assert.True((await new BootHost().RunAsync(pack.Options(directory: ignored))).Success);
        Assert.Equal(0, ignored.PinnedReads);
        Assert.Equal(0, ignored.ActiveReads);

        // Then the store's pinned version, which short circuits the active one.
        var pinned = new FakeVersionDirectory(pinned: BootPack.VersionNumber, active: 9);
        Assert.True((await new BootHost().RunAsync(pack.Options(configuredVersion: null, directory: pinned))).Success);
        Assert.Equal(1, pinned.PinnedReads);
        Assert.Equal(0, pinned.ActiveReads);

        // Then the active version.
        var active = new FakeVersionDirectory(pinned: null, active: BootPack.VersionNumber);
        Assert.True((await new BootHost().RunAsync(pack.Options(configuredVersion: null, directory: active))).Success);
        Assert.Equal(1, active.ActiveReads);
    }

    static void RegisterGameType(ContentTypeRegistry registry, IContentLoadIndex index)
    {
        var schema = new ContentFieldSchema(
            [new ContentFieldEntry("rate", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true)]);

        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            1024,
            "store",
            new PlainCodec(new ContentTypeId(1024), schema),
            validator: null,
            schema,
            ContentVisibility.ServerOnly,
            chunkSlots: 256,
            maxRowBytes: ContentPackFormat.DefaultMaxRowBytes,
            maxDefinitionId: null,
            loadIndex: index);
    }
}
