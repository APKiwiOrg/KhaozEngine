using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Upgrade;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

// Regression #1700. Operator policy must inspect the frozen publication, not an earlier preview.
public sealed class ConsumerPublishPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusalPreservesTheActiveVersionAndCompleteDraft(bool sqlite)
    {
        using var fixture = new ConsumerPublishPolicyFixture(sqlite);
        await fixture.SeedAsync();
        await fixture.EditAsync(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 12));
        ContentDraft before = Assert.IsType<ContentDraft>(await fixture.Store.GetOpenDraftAsync());

        ContentDraftCandidate preview = await fixture.PreviewAsync();
        Assert.False(preview.Report.IsValid);
        ConsumerPublishPolicyFixture.AssertGuard(Assert.Single(preview.Report.Findings));
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => fixture.Store.PublishAsync(fixture.Request()));

        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.Reason);
        ConsumerPublishPolicyFixture.AssertGuard(Assert.Single(refused.Findings));
        Assert.Equal(1, await fixture.Store.GetActiveVersionAsync());
        Assert.Single(await fixture.Store.ListVersionsAsync());
        ContentDraft after = Assert.IsType<ContentDraft>(await fixture.Store.GetOpenDraftAsync());
        Assert.Equal(before.BaseVersion, after.BaseVersion);
        Assert.Equal(before.OpenedBy, after.OpenedBy);
        Assert.Equal(before.OpenedAtUtc, after.OpenedAtUtc);
        Assert.Equal(before.Note, after.Note);
        Assert.Equal(before.Changes.Edits.Single().Fields, after.Changes.Edits.Single().Fields);
        Assert.Equal(before.EditCount, after.EditCount);
        Assert.True(before.TextState!.IsSameAs(after.TextState));
        Assert.False(after.IsFrozen);
        // Successful correction also proves the refusal released its publication freeze.
        await fixture.EditAsync(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 11));
        Assert.Equal(2, (await fixture.Store.PublishAsync(fixture.Request())).VersionNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEditAfterAPassingPreviewIsRefusedByTheFrozenPublisher(bool sqlite)
    {
        using var fixture = new ConsumerPublishPolicyFixture(sqlite);
        await fixture.SeedAsync();
        await fixture.EditAsync(PublishFixtures.LegacyField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1));
        Assert.True((await fixture.PreviewAsync()).Report.IsValid);

        await fixture.EditAsync(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 12));
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => fixture.Store.PublishAsync(fixture.Request()));

        ConsumerPublishPolicyFixture.AssertGuard(Assert.Single(refused.Findings));
        Assert.Equal(1, await fixture.Store.GetActiveVersionAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheSamePolicyAllowsAnUnguardedFieldChange(bool sqlite)
    {
        using var fixture = new ConsumerPublishPolicyFixture(sqlite);
        await fixture.SeedAsync();
        await fixture.EditAsync(PublishFixtures.LegacyField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1));

        Assert.True((await fixture.PreviewAsync()).Report.IsValid);
        Assert.Equal(2, (await fixture.Store.PublishAsync(fixture.Request())).VersionNumber);
        ContentRow row = Assert.Single((await fixture.Store.ListRowsAsync(fixture.Type, 2, null, true, 0, 10)).Rows);
        Assert.Equal(11, row.Fields[0].Number);
        Assert.Equal(1, row.Fields[1].Number);
    }

    [Fact]
    public async Task RowOnlyPublicationEvaluatesTheDraftReadUnderItsFreeze()
    {
        using var fixture = new ConsumerPublishPolicyFixture(false);
        await fixture.SeedAsync();
        await fixture.EditAsync(PublishFixtures.LegacyField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1));
        Assert.True((await fixture.PreviewAsync()).Report.IsValid);
        var store = new EditBeforePolicyFreezeStore(fixture.Store,
            () => fixture.EditAsync(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 12)));
        var commit = new ContentPublishCommit(store, fixture.Packs,
            new ContentPublisher(store, (IContentIdPersistence)fixture.Store, fixture.Registry));

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => commit.PublishAsync(fixture.Request()));

        ConsumerPublishPolicyFixture.AssertGuard(Assert.Single(refused.Findings));
        Assert.Equal(1, await fixture.Store.GetActiveVersionAsync());
        Assert.False((await fixture.Store.GetOpenDraftAsync())!.IsFrozen);
    }

    [Fact]
    public async Task TrustedUpgradePublishesAGuardedChangeWithoutTheOperatorPolicy()
    {
        using var fixture = new ConsumerPublishPolicyFixture(false);
        await fixture.SeedAsync();
        await fixture.EditAsync(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 12));
        Assert.False((await fixture.PreviewAsync()).Report.IsValid);
        await fixture.Store.DiscardDraftAsync(PublishFixtures.Actor, "operator");
        ContentBundle target = UpgradeFixtures.Target(fixture.Registry,
            UpgradeFixtures.Row(fixture.Type, 1, ConsumerPublishPolicyFixture.Key, 12));
        var upgrades = new ContentUpgradeSet(UpgradeFixtures.PatchesBothFields(
            "replace-artifacts", 1, target, fixture.Type, ConsumerPublishPolicyFixture.Key, 11, 12));

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            fixture.Store, fixture.Registry, upgrades, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(2, await fixture.Store.GetActiveVersionAsync());
        Assert.Null(await fixture.Store.GetOpenDraftAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalPolicyPreservesPublishedBytesAndVersions(bool configuredPolicy)
    {
        using var plain = new ConsumerPublishPolicyFixture(false);
        using var configured = new ConsumerPublishPolicyFixture(false);
        await plain.SeedAsync();
        await configured.SeedAsync();
        await plain.EditAsync(PublishFixtures.LegacyField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1));
        await configured.EditAsync(PublishFixtures.LegacyField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1));

        ContentPublishResult expected = await plain.Store.PublishAsync(PublishFixtures.Request(1));
        ContentPublishResult actual = await configured.Store.PublishAsync(configured.Request() with { PublishPolicy = configuredPolicy ? configured.Policy : null });

        Assert.Equal(expected.VersionNumber, actual.VersionNumber);
        Assert.Equal(expected.ServerManifestHash, actual.ServerManifestHash);
        Assert.Equal(expected.ClientManifestHash, actual.ClientManifestHash);
        string[] hashes = await ConsumerPublishPolicyFixture.HashesAsync(plain.Packs, 2);
        Assert.NotEmpty(hashes);
        Assert.Equal(hashes, await ConsumerPublishPolicyFixture.HashesAsync(configured.Packs, 2));
        foreach (string hash in hashes)
        {
            Assert.Equal((await plain.Packs.GetAsync(hash))!.Value.ToArray(),
                (await configured.Packs.GetAsync(hash))!.Value.ToArray());
        }
    }
}
