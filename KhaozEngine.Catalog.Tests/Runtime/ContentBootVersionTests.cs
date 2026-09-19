using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Tests.Catalog.Store;
using KhaozEngine.Tests.Catalog.Validation;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// Boot step 2 read on its own, through the member a CALLER reads it with. The same precedence is asserted
/// through <c>RunAsync</c> in <c>ContentBootTests</c>, and that one can only reach versions the fixture
/// actually published, because every arm of it has to load a pack afterwards. This one reads the resolver
/// directly, so it also pins the arms a boot cannot reach: a pinned value that is not a version, and a
/// server that hands the boot no directory at all.
/// </summary>
public sealed class ContentBootVersionTests
{
    /// <summary>The build the options carry, which step 2 never looks at.</summary>
    const int ServerBuild = 20;

    [Fact]
    public async Task TheResolvedVersionIsTheConfiguredOneThenThePinnedOneThenTheActiveOne()
    {
        using var root = new TemporaryRoot();

        // The server's own config pin wins ALWAYS, and the directory is not read at all.
        var ignored = new FakeVersionDirectory(pinned: 9, active: 8);
        Assert.Equal(4, await ContentBoot.ResolveVersionAsync(Options(root, configuredVersion: 4, directory: ignored)));
        Assert.Equal(0, ignored.PinnedReads);
        Assert.Equal(0, ignored.ActiveReads);

        // Then the directory's pinned version, which short circuits the active one.
        var pinned = new FakeVersionDirectory(pinned: 6, active: 8);
        Assert.Equal(6, await ContentBoot.ResolveVersionAsync(Options(root, configuredVersion: null, directory: pinned)));
        Assert.Equal(1, pinned.PinnedReads);
        Assert.Equal(0, pinned.ActiveReads);

        // Then the active version.
        var active = new FakeVersionDirectory(pinned: null, active: 8);
        Assert.Equal(8, await ContentBoot.ResolveVersionAsync(Options(root, configuredVersion: null, directory: active)));
        Assert.Equal(1, active.PinnedReads);
        Assert.Equal(1, active.ActiveReads);

        // A pinned value that is not a version number is no pin, and falls through the same way a null does.
        var zero = new FakeVersionDirectory(pinned: 0, active: 8);
        Assert.Equal(8, await ContentBoot.ResolveVersionAsync(Options(root, configuredVersion: null, directory: zero)));
        Assert.Equal(1, zero.ActiveReads);

        var negative = new FakeVersionDirectory(pinned: -3, active: 8);
        Assert.Equal(8, await ContentBoot.ResolveVersionAsync(Options(root, configuredVersion: null, directory: negative)));
        Assert.Equal(1, negative.ActiveReads);

        // No config pin and no directory resolves 0, which is the boot's step 2 refusal and, for a caller
        // preparing a pack store, the version it must not go on to rebuild.
        Assert.Equal(0, await ContentBoot.ResolveVersionAsync(Options(root, configuredVersion: null, directory: null)));

        await Assert.ThrowsAsync<ArgumentNullException>(() => ContentBoot.ResolveVersionAsync(null!));
    }

    /// <summary>
    /// The options as step 2 needs them. The store is a real one on disk because the type requires one, and
    /// nothing the resolver does reads it.
    /// </summary>
    static ContentBootOptions Options(
        TemporaryRoot root,
        int? configuredVersion,
        IContentVersionDirectory? directory)
        => new()
        {
            Registry = ContentValidationFixtures.EngineRegistry(),
            Store = new FileSystemPackStore(root.Path),
            Holder = new ContentRuntimeHolder(),
            ServerBuild = ServerBuild,
            ConfiguredVersion = configuredVersion,
            Directory = directory,
        };
}
