using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The three reads the boot makes of the HOST's own providers, the directory's pinned and active versions at
/// step 2 and the version record's hashes at step 3, when the provider throws. The boot promises a result and
/// never an exception, so each is a <see cref="ContentBootRefusal.VersionSourceUnreadable"/> refusal with one
/// line, and the caller's own cancellation is the one thing that still propagates.
/// </summary>
public sealed class ContentBootSourceFaultTests
{
    /// <summary>Which of the three reads a test breaks.</summary>
    public enum Read
    {
        /// <summary>The directory's operator pin, step 2.</summary>
        Pinned,

        /// <summary>The directory's active version, step 2.</summary>
        Active,

        /// <summary>The version record's two manifest hashes, step 3.</summary>
        Hashes,
    }

    /// <summary>A throwing read is a refusal naming the read and the fault, and nothing is published.</summary>
    [Theory]
    [InlineData(Read.Pinned, 2, "version directory")]
    [InlineData(Read.Active, 2, "version directory")]
    [InlineData(Read.Hashes, 3, "version record for version 7")]
    public async Task AThrowingReadRefusesRatherThanEscaping(Read read, int step, string named)
    {
        using BootPack pack = await BootPack.CreateAsync();
        var source = new BreakingSource(pack, read, () => new InvalidOperationException("the database went away."));

        var host = new BootHost();
        ContentBootResult refused = await host.RunAsync(pack.Options(configuredVersion: null, directory: source));

        Assert.False(refused.Success);
        Assert.Equal(ContentBootRefusal.VersionSourceUnreadable, refused.Refusal);
        Assert.Equal(step, refused.Step);
        Assert.Equal(3, host.ExitCode);
        Assert.Equal($"content: {named} unreadable (InvalidOperationException: the database went away).", host.Line);
        Assert.False(pack.Holder.IsLoaded);
        Assert.False(refused.PackPointerCrossChecked);
    }

    /// <summary>
    /// The caller's cancellation, raised INSIDE the read so every earlier step ran with a live token, still
    /// propagates from each of the three rather than becoming a refusal nobody is waiting for.
    /// </summary>
    [Theory]
    [InlineData(Read.Pinned)]
    [InlineData(Read.Active)]
    [InlineData(Read.Hashes)]
    public async Task TheCallersCancellationPropagatesFromEachRead(Read read)
    {
        using BootPack pack = await BootPack.CreateAsync();
        using var cancel = new CancellationTokenSource();
        var source = new BreakingSource(pack, read, fault: null, cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ContentBoot.RunAsync(pack.Options(configuredVersion: null, directory: source), cancel.Token));
        Assert.Equal(1, source.BrokenReads);
        Assert.False(pack.Holder.IsLoaded);
    }

    /// <summary>
    /// A cancellation the CALLER did not ask for, which is what a driver's own timeout looks like, is a fault
    /// of the provider and refuses like any other.
    /// </summary>
    [Fact]
    public async Task ACancellationTheCallerDidNotAskForRefuses()
    {
        using BootPack pack = await BootPack.CreateAsync();
        var source = new BreakingSource(pack, Read.Hashes, () => new TaskCanceledException("timed out"));

        var host = new BootHost();
        ContentBootResult refused = await host.RunAsync(pack.Options(configuredVersion: null, directory: source));

        Assert.Equal(ContentBootRefusal.VersionSourceUnreadable, refused.Refusal);
        Assert.Equal("content: version record for version 7 unreadable (TaskCanceledException: timed out).", host.Line);
    }

    /// <summary>A driver's multi-line message still makes exactly one operator line.</summary>
    [Fact]
    public async Task AMultiLineMessageStaysOneLine()
    {
        using BootPack pack = await BootPack.CreateAsync();
        var source = new BreakingSource(
            pack,
            Read.Active,
            () => new InvalidOperationException("login failed" + Environment.NewLine + "server closed the connection"));

        var host = new BootHost();
        _ = await host.RunAsync(pack.Options(configuredVersion: null, directory: source));

        Assert.Equal(
            "content: version directory unreadable (InvalidOperationException: login failed server closed the connection).",
            host.Line);
    }

    /// <summary>
    /// <see cref="ContentBoot.ResolveVersionAsync"/> answers a NUMBER and has no refusal to put a fault in, so
    /// the directory's exception reaches its caller unchanged.
    /// </summary>
    [Fact]
    public async Task ResolveVersionPropagatesTheDirectorysFault()
    {
        using BootPack pack = await BootPack.CreateAsync();
        var source = new BreakingSource(pack, Read.Pinned, () => new InvalidOperationException("gone"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ContentBoot.ResolveVersionAsync(pack.Options(configuredVersion: null, directory: source)));
    }

    /// <summary>
    /// A directory and hash source over one healthy pack that breaks exactly ONE read, either by throwing a
    /// fault or by cancelling the caller's token and throwing for it.
    /// </summary>
    sealed class BreakingSource(
        BootPack pack,
        Read broken,
        Func<Exception>? fault,
        CancellationTokenSource? cancel = null) : IContentVersionDirectory, IContentVersionHashSource
    {
        /// <summary>How many times the broken read was reached.</summary>
        public int BrokenReads { get; private set; }

        /// <inheritdoc />
        public Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
        {
            Break(Read.Pinned, cancellationToken);
            return Task.FromResult<int?>(null);
        }

        /// <inheritdoc />
        public Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        {
            Break(Read.Active, cancellationToken);
            return Task.FromResult(BootPack.VersionNumber);
        }

        /// <inheritdoc />
        public Task<ContentVersionHashes?> GetVersionHashesAsync(
            int versionNumber,
            CancellationToken cancellationToken = default)
        {
            Break(Read.Hashes, cancellationToken);
            return Task.FromResult<ContentVersionHashes?>(
                new ContentVersionHashes(pack.ManifestHash, pack.ClientManifestHash));
        }

        void Break(Read read, CancellationToken cancellationToken)
        {
            if (read != broken)
            {
                return;
            }

            BrokenReads++;
            if (cancel is not null)
            {
                cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw fault!();
        }
    }
}
