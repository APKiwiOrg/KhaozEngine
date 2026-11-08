using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>The script guards refuse before forwarding a write, and readiness keeps its producer's outcome.</summary>
public sealed class CatalogHarnessRefusalTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AFreezeOutsideItsScriptLeavesTheDraftUnchanged(bool sqlite, bool countExceeded)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, sqlite);
        await lease.Store.ApplyEditsAsync(
            [ContentEdit.Add(UpgradeFixtures.Other, new ContentKey("next_row"), PublishFixtures.Fields(22))],
            UpgradeFixtures.Actor, UpgradeFixtures.Operator, "script guard fixture");
        var script = new ScriptedUpgradeStore(lease.Store, 1, countExceeded ? 1 : 2);
        if (countExceeded)
        {
            // The first call, exactly at the one-freeze limit, must reach the real store.
            await script.FreezeDraftAsync(1);
            Assert.True((await lease.Store.GetOpenDraftAsync())!.IsFrozen);
            await lease.Store.ClearDraftFreezeAsync();
        }
        ContentDraft before = (await lease.Store.GetOpenDraftAsync())!;

        var failure = await Assert.ThrowsAsync<CatalogScriptException>(() => script.FreezeDraftAsync(1));

        Assert.IsAssignableFrom<InvalidOperationException>(failure);
        Assert.Equal(countExceeded ? CatalogScriptRefusal.FreezeCountExceeded
            : CatalogScriptRefusal.FreezeBaseVersionMismatch, failure.Reason);
        Assert.Equal(countExceeded ? 1 : 0, failure.ActualIndex);
        if (countExceeded)
        {
            Assert.Equal(1, failure.MaximumCount);
            Assert.Null(failure.ExpectedBaseVersion);
            Assert.Null(failure.ActualBaseVersion);
        }
        else
        {
            Assert.Equal(2, failure.ExpectedBaseVersion);
            Assert.Equal(1, failure.ActualBaseVersion);
            Assert.Null(failure.MaximumCount);
        }
        ContentDraft after = (await lease.Store.GetOpenDraftAsync())!;
        Assert.Equal((before.BaseVersion, before.OpenedBy, before.OpenedAtUtc, before.Note, before.FrozenForBaseVersion),
            (after.BaseVersion, after.OpenedBy, after.OpenedAtUtc, after.Note, after.FrozenForBaseVersion));
        Assert.Equal(before.TextState?.Edits, after.TextState?.Edits);
        Assert.Equal(before.TextState?.Introductions, after.TextState?.Introductions);
        ContentEdit beforeEdit = Assert.Single(before.Changes.Edits);
        ContentEdit afterEdit = Assert.Single(after.Changes.Edits);
        Assert.Equal((beforeEdit.Type, beforeEdit.DefinitionId, beforeEdit.Key, beforeEdit.Operation,
                beforeEdit.RetirePolicy, beforeEdit.ReplacementId, beforeEdit.ForkKey, beforeEdit.ForkFlagField,
                beforeEdit.FamilyId, beforeEdit.ImportedAsRetired),
            (afterEdit.Type, afterEdit.DefinitionId, afterEdit.Key, afterEdit.Operation,
                afterEdit.RetirePolicy, afterEdit.ReplacementId, afterEdit.ForkKey, afterEdit.ForkFlagField,
                afterEdit.FamilyId, afterEdit.ImportedAsRetired));
        Assert.Equal(beforeEdit.Fields, afterEdit.Fields);

        // Fix only the missing script slot or the expected base, after proving no write escaped.
        var control = new ScriptedUpgradeStore(lease.Store, 1, countExceeded ? [1, 1] : [1]);
        await control.FreezeDraftAsync(1);
        if (countExceeded)
        {
            await control.FreezeDraftAsync(1);
        }
        Assert.True((await lease.Store.GetOpenDraftAsync())!.IsFrozen);
    }

    [Fact]
    public void ReportLinesAreAnOwnedReadOnlySnapshot()
    {
        string[] source = ["first diagnostic", "second diagnostic"];
        var failure = new CatalogScriptException(CatalogScriptRefusal.RunnerCompletedBeforePhase, "refused")
        { ReportLines = source };
        source[0] = "changed";

        Assert.Equal(new[] { "first diagnostic", "second diagnostic" }, failure.ReportLines);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)failure.ReportLines!)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((ICollection)failure.ReportLines!).SyncRoot);
    }

    [Fact]
    public async Task ACompletedRunnerCannotStandInForAMissingReadinessSignal()
    {
        var owner = new CatalogRunnerLifetime();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ContentUpgradeReport> producer = owner.Start(_ => Task.FromResult(new ContentUpgradeReport(
            ContentUpgradeOutcome.UpToDate, 1, 1, [], [])));
        try
        {
            var failure = await Assert.ThrowsAsync<CatalogScriptException>(
                () => owner.ReadyAsync(ready.Task, producer, CatalogPhase.FirstCommit));

            Assert.IsAssignableFrom<InvalidOperationException>(failure);
            Assert.Equal(CatalogScriptRefusal.RunnerCompletedBeforePhase, failure.Reason);
            Assert.Equal(CatalogPhase.FirstCommit, failure.Phase);
            Assert.False(owner.Diagnostic.Task.IsCompleted);

            ready.TrySetResult();
            await owner.ReadyAsync(ready.Task, producer, CatalogPhase.FirstCommit);
        }
        finally
        {
            await owner.FinishAsync();
        }
    }
}
