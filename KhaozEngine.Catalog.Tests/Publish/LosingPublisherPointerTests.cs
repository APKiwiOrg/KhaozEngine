using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Two publishers that prepared the SAME version number from the same base, where the one that finishes its
/// prepare first goes on to lose. That is two replicas of different builds booting together: each publishes the
/// same upgrade with its own minimum build, so the two plans carry different manifests under one number.
/// <para>
/// <b>The interleaving is placed, not hoped for.</b> The loser's step hook runs the winner's whole publish at
/// the end of the loser's prepare, so the winner has committed and swept before the loser writes a single file.
/// Everything the loser does after that is exactly what a slow publisher does.
/// </para>
/// <para>
/// <b>What has to hold is that the committed version's pointer still names the committed manifests.</b> The
/// boot reads that pointer to decide what to serve, and the sweep reads it to decide what to keep, so a pointer
/// naming the loser's manifests serves content that never committed and deletes content that did.
/// </para>
/// </summary>
public class LosingPublisherPointerTests
{
    [Theory]
    [InlineData(CrashStore.InMemory)]
    [InlineData(CrashStore.Sqlite)]
    public async Task ALosingPublisherLeavesTheCommittedVersionsPointerNamingTheCommittedManifests(CrashStore kind)
    {
        using CrashSafetyHarness harness = await CrashSafetyHarness.StartAsync(kind);
        await harness.ApplyAsync(CrashSafetyHarness.Reprice());

        ContentPublishResult? won = null;
        var loserPack = new CountingPackStore(harness.Pack);
        var loser = new ContentPublishCommit(
            harness.Store,
            loserPack,
            new ContentPublisher(
                harness.Store,
                (IContentIdPersistence)harness.Store,
                harness.Registry,
                step =>
                {
                    if (step == ContentPublishStep.AfterManifestWrite && won is null)
                    {
                        // The winner, start to finish, while the loser holds a finished plan for the same
                        // number and has not written anything yet.
                        won = harness.Commit().PublishAsync(CrashSafetyHarness.Request(1)).GetAwaiter().GetResult();
                    }
                }));

        var loserRequest = new ContentPublishRequest(
            PublishFixtures.Actor, "oid:tests", "the other build", 1, MinimumServerBuild: 7);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => Task.Run(() => loser.PublishAsync(loserRequest)));

        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, refused.Reason);
        Assert.NotNull(won);
        Assert.Equal(2, won.VersionNumber);

        // The loser wrote its files, which are inert, and no pointer at all.
        Assert.NotEmpty(loserPack.Writes);
        Assert.DoesNotContain(CountingPackStore.PointerWrite, loserPack.Writes);

        ContentVersionRecord? committed = await harness.Store.GetVersionAsync(2);
        Assert.NotNull(committed);
        PackVersionPointer? pointer = await harness.Pack.GetVersionPointerAsync(2);
        Assert.NotNull(pointer);
        Assert.Equal(committed.ServerManifestHash, pointer.ServerManifestHash);
        Assert.Equal(committed.ClientManifestHash, pointer.ClientManifestHash);

        // What the sweep and the boot read through that pointer is the committed version.
        await harness.AssertEveryReferencedFileServesAsync();
        var listed = new List<string>();
        await foreach (string hash in harness.Pack.ListAsync(2))
        {
            listed.Add(hash);
        }

        Assert.Contains(committed.ServerManifestHash, listed);
    }
}
