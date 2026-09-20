using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The one pointer the boot used to take on trust: the <c>versions/&lt;n&gt;</c> file, which is the only
/// object in a content-addressed store that is not named by its own hash and so the only one a stale pack
/// root can still answer with.
/// <para>
/// A catalog REPLACED at the same version number is an ordinary event for a game that ships its pack in the
/// client build, and the old root serving the old manifest under the new number is not a refusal anywhere
/// else on the path: the manifest digests to its own name, declares the right number, decodes, and every
/// chunk verifies. Only the version record knows which manifest that number is supposed to mean.
/// </para>
/// </summary>
public sealed class ContentBootPointerTests
{
    /// <summary>The build the boots are told they are running, over the fixture's minimum of 0.</summary>
    const int ServerBuild = 20;

    /// <summary>The version both catalogs publish, which is the whole point: one number, two contents.</summary>
    const int VersionNumber = 1;

    /// <summary>What the refusal line calls the store the pointer was read from.</summary>
    const string StoreName = "the stale store";

    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    /// <summary>
    /// The defect: catalog A's pack root beside catalog B's version record, both at version 1. The pointer
    /// still names A's manifest, A's chunks are all present, and the connect door would advertise B's client
    /// hash over A's rows. The boot refuses before it fetches anything.
    /// </summary>
    [Fact]
    public async Task APointerNamingAnotherCatalogsManifestAtTheSameVersionRefuses()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        var packB = new FileSystemPackStore(rootB.Path);
        ContentPublishResult a = await PublishAsync(registry, packA, value: 11);
        InMemoryContentAuthoringStore storeB = PublishFixtures.Store(registry, packB);
        ContentPublishResult b = await PublishAsync(storeB, value: 99);

        Assert.Equal(VersionNumber, a.VersionNumber);
        Assert.Equal(VersionNumber, b.VersionNumber);
        Assert.NotEqual(a.ServerManifestHash, b.ServerManifestHash);

        var host = new BootHost();
        var holder = new ContentRuntimeHolder();
        ContentBootResult refused = await host.RunAsync(Options(registry, packA, storeB, holder));

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.PackPointerMismatch, refused.Refusal);
        Assert.Equal(3, refused.Step);
        Assert.Equal(
            FormattableString.Invariant(
                $"content: version {VersionNumber} in {StoreName} points at server manifest {a.ServerManifestHash}, ")
                + FormattableString.Invariant($"the version record names {b.ServerManifestHash}."),
            host.Line);
        Assert.False(holder.IsLoaded);
        Assert.Null(refused.Runtime);
    }

    /// <summary>
    /// The pointer's CLIENT half, which the boot itself never fetches and the publish sweep's keep set does.
    /// A pointer half the version record disagrees with is the same stale pointer either way, so the boot
    /// refuses on it rather than loading a pack whose other half the sweep would walk.
    /// </summary>
    [Fact]
    public async Task APointerNamingAnotherCatalogsClientManifestRefuses()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore storeA = PublishFixtures.Store(registry, packA);
        ContentPublishResult a = await PublishAsync(storeA, value: 11);
        ContentPublishResult b = await PublishAsync(registry, new FileSystemPackStore(rootB.Path), value: 99);

        Assert.NotEqual(a.ClientManifestHash, b.ClientManifestHash);

        // The server half is the one this root really holds, so nothing but the client half disagrees.
        await packA.PutVersionPointerAsync(VersionNumber, a.ServerManifestHash, b.ClientManifestHash);

        var host = new BootHost();
        ContentBootResult refused = await host.RunAsync(
            Options(registry, packA, storeA, new ContentRuntimeHolder()));

        Assert.Equal(3, host.ExitCode);
        Assert.Equal(ContentBootRefusal.PackPointerMismatch, refused.Refusal);
        Assert.Equal(
            FormattableString.Invariant(
                $"content: version {VersionNumber} in {StoreName} points at client manifest {b.ClientManifestHash}, ")
                + FormattableString.Invariant($"the version record names {a.ClientManifestHash}."),
            host.Line);
    }

    /// <summary>
    /// The pack root the record does describe, booted through the same comparison, because a check that
    /// refuses the healthy pack is worse than the hole it closes.
    /// </summary>
    [Fact]
    public async Task APointerTheVersionRecordAgreesWithBootsAsBefore()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);
        ContentPublishResult published = await PublishAsync(store, value: 11);

        var host = new BootHost();
        var holder = new ContentRuntimeHolder();
        ContentBootResult booted = await host.RunAsync(Options(registry, pack, store, holder));

        Assert.True(booted.Success, string.Join(" | ", host.Lines));
        Assert.Equal(0, host.ExitCode);
        Assert.NotNull(booted.Runtime);
        Assert.Equal(VersionNumber, booted.Runtime.VersionNumber);
        Assert.Equal(published.ServerManifestHash, booted.Runtime.Identity.ManifestHash);
        Assert.True(holder.IsLoaded);
    }

    /// <summary>
    /// A directory that carries version NUMBERS and no hashes, which is what the seam requires of every
    /// implementation. The comparison is skipped for it and the stale pack root loads exactly as it did
    /// before, because the fact that would refuse it is not reachable from what the boot was handed.
    /// </summary>
    [Fact]
    public async Task ADirectoryThatCarriesNoHashesSkipsTheComparison()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        ContentPublishResult a = await PublishAsync(registry, packA, value: 11);
        _ = await PublishAsync(registry, new FileSystemPackStore(rootB.Path), value: 99);

        var numbers = new FakeVersionDirectory(pinned: null, active: VersionNumber);
        var host = new BootHost();
        ContentBootResult booted = await host.RunAsync(
            Options(registry, packA, numbers, new ContentRuntimeHolder()));

        Assert.True(booted.Success, string.Join(" | ", host.Lines));
        Assert.Equal(a.ServerManifestHash, booted.Runtime!.Identity.ManifestHash);

        // And the other deployment with no fact to compare against: a config pin and no directory at all.
        var pinned = new BootHost();
        ContentBootResult second = await pinned.RunAsync(new ContentBootOptions
        {
            Registry = registry,
            Store = packA,
            Holder = new ContentRuntimeHolder(),
            ServerBuild = ServerBuild,
            ConfiguredVersion = VersionNumber,
            StoreName = StoreName,
        });

        Assert.True(second.Success, string.Join(" | ", pinned.Lines));
    }

    /// <summary>
    /// One catalog published at version 1 into its own pack root, through the shipped publisher, so the
    /// version record and the pointer are written by the code that really writes them.
    /// </summary>
    static async Task<ContentPublishResult> PublishAsync(
        InMemoryContentAuthoringStore store,
        int value)
    {
        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(value)));
        return await store.PublishAsync(PublishFixtures.Request(0));
    }

    static Task<ContentPublishResult> PublishAsync(ContentTypeRegistry registry, IPackStore pack, int value)
        => PublishAsync(PublishFixtures.Store(registry, pack), value);

    /// <summary>
    /// The boot as a server that reads its authoring database runs it: no config pin, so the version comes
    /// from the directory, and the pack store answers its own pointer.
    /// </summary>
    static ContentBootOptions Options(
        ContentTypeRegistry registry,
        FileSystemPackStore pack,
        IContentVersionDirectory directory,
        ContentRuntimeHolder holder)
        => new()
        {
            Registry = registry,
            Store = pack,
            Holder = holder,
            ServerBuild = ServerBuild,
            ConfiguredVersion = null,
            Directory = directory,
            StoreName = StoreName,
        };
}
