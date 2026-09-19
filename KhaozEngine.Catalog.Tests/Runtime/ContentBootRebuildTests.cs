using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The two halves of the recovery joined end to end: a boot against a pack root that lost its files refuses,
/// a rebuild out of the authoring store fills that root, and the boot then loads the SAME version the
/// original root serves.
/// <para>
/// Every other rebuild fact is asserted against the pack store, which is one side of the claim. This one is
/// asserted against the READER, because what an operator recovering a server cares about is not that the
/// bytes are present at the right addresses but that <c>ContentBoot</c> stops refusing and hands back a
/// runtime that answers the same way.
/// </para>
/// </summary>
public sealed class ContentBootRebuildTests
{
    /// <summary>The build the boots are told they are running, comfortably over the version's minimum of 0.</summary>
    const int ServerBuild = 20;

    /// <summary>The version the fixture publishes, which is the first a fresh store hands out.</summary>
    const int VersionNumber = 1;

    /// <summary>What the refusal line calls the empty root, so the assertion reads as the operator's line.</summary>
    const string StoreName = "the recovered store";

    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    [Fact]
    public async Task ABootAgainstAnEmptyPackRootRefusesAndSucceedsAfterTheActiveVersionIsRebuiltIntoIt()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, packA);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11)),
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(22)),
            ContentEdit.Import(Thing, PublishFixtures.SecondChunkId, new ContentKey("far"), PublishFixtures.Fields(33)));
        ContentPublishResult published = await store.PublishAsync(PublishFixtures.Request(0));
        Assert.Equal(VersionNumber, published.VersionNumber);

        // The server as it was: the pack root the publish wrote, which boots.
        var fromA = new BootHost();
        var holderA = new ContentRuntimeHolder();
        ContentBootResult original = await fromA.RunAsync(Options(registry, packA, holderA));

        Assert.True(original.Success, string.Join(" | ", fromA.Lines));
        Assert.Equal(0, fromA.ExitCode);
        Assert.NotNull(original.Runtime);

        // The same server after its pack root did not outlive the process. The authoring store still names
        // version 1 as active, and there is no manifest to fetch, so the boot refuses at step 3.
        var packB = new FileSystemPackStore(rootB.Path);
        var refusedHost = new BootHost();
        var holderB = new ContentRuntimeHolder();
        ContentBootResult refused = await refusedHost.RunAsync(Options(registry, packB, holderB));

        Assert.Equal(3, refusedHost.ExitCode);
        Assert.Equal(ContentBootRefusal.ManifestUnreadable, refused.Refusal);
        Assert.Equal(3, refused.Step);
        Assert.Equal(
            FormattableString.Invariant($"content: manifest for version {VersionNumber} absent from {StoreName}."),
            refusedHost.Line);
        Assert.False(holderB.IsLoaded);

        ContentPackRebuildResult rebuilt = await ContentPackRebuild
            .RunAsync(store, registry, VersionNumber, packB);
        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail ?? rebuilt.RefusalReason);

        // And the same boot, against the same root, with nothing else changed.
        var recovered = new BootHost();
        var holderC = new ContentRuntimeHolder();
        ContentBootResult second = await recovered.RunAsync(Options(registry, packB, holderC));

        Assert.True(second.Success, string.Join(" | ", recovered.Lines));
        Assert.Equal(0, recovered.ExitCode);
        Assert.NotNull(second.Runtime);
        Assert.True(holderC.IsLoaded);

        // The version the runtime announces, and the manifest hash it announces it by, are the originals.
        Assert.Equal(original.Runtime.VersionNumber, second.Runtime.VersionNumber);
        Assert.Equal(VersionNumber, second.Runtime.VersionNumber);
        Assert.Equal(original.Runtime.Identity.ManifestHash, second.Runtime.Identity.ManifestHash);
        Assert.Equal(published.ServerManifestHash, second.Runtime.Identity.ManifestHash);

        // And a row read through the recovered runtime is the row the original serves, key, id and bytes.
        Assert.True(original.Runtime.TryGetId(Thing, new ContentKey("two"), out int expectedId));
        Assert.True(second.Runtime.TryGetId(Thing, new ContentKey("two"), out int actualId));
        Assert.Equal(expectedId, actualId);
        Assert.True(
            original.Runtime.Body(Thing, expectedId).SequenceEqual(second.Runtime.Body(Thing, actualId)),
            "The recovered row's bytes are not the published row's.");
        Assert.Equal(
            original.Runtime.Rows(Thing).Count,
            second.Runtime.Rows(Thing).Count);
    }

    /// <summary>
    /// The boot as a recovering server runs it: a config pin rather than an authoring directory, because the
    /// pack root is what was lost and the version number was never in doubt.
    /// </summary>
    static ContentBootOptions Options(
        ContentTypeRegistry registry,
        FileSystemPackStore pack,
        ContentRuntimeHolder holder)
        => new()
        {
            Registry = registry,
            Store = pack,
            Holder = holder,
            ServerBuild = ServerBuild,
            ConfiguredVersion = VersionNumber,
            StoreName = StoreName,
        };
}
