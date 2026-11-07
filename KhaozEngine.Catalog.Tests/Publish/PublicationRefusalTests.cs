using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

public sealed class PublicationRefusalTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);
    static ContentKey Key => new("one");

    [Fact]
    public void PreviewRefusesACarriedIdAlreadyInTheBaseline()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        ContentPublishBaseline baseline = Baseline();
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentDraftCandidate.Build(baseline, new ContentChangeSet([
                ContentEdit.Import(Thing, 1, new ContentKey("other"), PublishFixtures.Fields(2))]), registry));

        Assert.Equal(ContentAuthoringRefusal.DefinitionIdDuplicate, refused.Refusal);
        Assert.Equal(ContentAuthoringException.EditTargetCollisionReason, refused.Reason);
        Assert.Equal(Thing, refused.Type);
        Assert.Equal(1, refused.Id);
        Assert.Equal("other", refused.RowKey);
        Assert.Equal(0, refused.EditOrdinal);
        Assert.True(ContentDraftCandidate.Build(baseline, new ContentChangeSet([
            ContentEdit.Import(Thing, 2, new ContentKey("other"), PublishFixtures.Fields(2))]), registry).Report.IsValid);
    }

    [Fact]
    public void PreviewRefusesAnUnknownFieldWithItsEditContext()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        ContentFieldEdit bad = new("missing", ContentFieldValue.OfNumber(ContentFieldKind.Int, 2));
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentDraftCandidate.Build(Baseline(), new ContentChangeSet([
                ContentEdit.Update(Thing, 1, Key, [bad])]), registry));

        Assert.Equal(ContentAuthoringRefusal.FieldUnknown, refused.Refusal);
        Assert.Equal(ContentAuthoringException.UnknownFieldReason, refused.Reason);
        Assert.Equal(Thing, refused.Type);
        Assert.Equal(1, refused.Id);
        Assert.Equal("thing", refused.TypeKey);
        Assert.Equal("missing", refused.FieldName);
        Assert.Equal(0, refused.EditOrdinal);
        Assert.True(ContentDraftCandidate.Build(Baseline(), new ContentChangeSet([
            ContentEdit.Update(Thing, 1, Key, PublishFixtures.Fields(2))]), registry).Report.IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewRefusesAnUnknownUpdateOrRetireTarget(bool retire)
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        ContentEdit edit = retire
            ? ContentEdit.Retire(Thing, 1, Key, ContentRetirePolicy.Placeholder, 0)
            : ContentEdit.Update(Thing, 1, Key, PublishFixtures.Fields(2));
        var changes = new ContentChangeSet([edit]);
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentDraftCandidate.Build(ContentPublishBaseline.Empty, changes, registry));

        Assert.Equal(ContentAuthoringRefusal.RowUnknown, refused.Refusal);
        Assert.Equal(ContentAuthoringException.UnknownRowReason, refused.Reason);
        Assert.Equal(Thing, refused.Type);
        Assert.Equal(1, refused.Id);
        Assert.Equal("one", refused.RowKey);
        Assert.Equal(0, refused.EditOrdinal);
        Assert.True(ContentDraftCandidate.Build(Baseline(), changes, registry).Report.IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewRefusesAnUnregisteredBaselineOrEditType(bool inBaseline)
    {
        ContentPublishBaseline baseline = inBaseline ? Baseline() : ContentPublishBaseline.Empty;
        var changes = new ContentChangeSet(inBaseline ? [] : [ContentEdit.Add(Thing, Key, PublishFixtures.Fields(1))]);
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentDraftCandidate.Build(baseline, changes, new ContentTypeRegistry()));

        Assert.Equal(ContentAuthoringRefusal.TypeUnknown, refused.Refusal);
        Assert.Equal(ContentAuthoringException.UnknownTypeReason, refused.Reason);
        Assert.Equal(Thing, refused.Type);
        Assert.True(ContentDraftCandidate.Build(baseline, changes,
            PublishFixtures.Registry(PublishFixtures.Thing)).Report.IsValid);
    }

    [Fact]
    public async Task ARowOnlyPolicyPublishRefusesALostFreezeAndCanRetryWithTheMarkerHeld()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore inner = PublishFixtures.Store(registry);
        await PublishFixtures.ApplyAsync(inner, ContentEdit.Add(Thing, Key, PublishFixtures.Fields(1)));
        var store = new LostFreezeStore(inner);
        var publisher = new ContentPublisher(store, inner, registry);
        ContentPublishRequest request = PublishFixtures.Request(0) with { PublishPolicy = new FindingsPolicy(false) };
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            publisher.PrepareAsync(request, ContentPublishBaseline.Empty));

        Assert.Equal(ContentAuthoringRefusal.FrozenDraftLost, refused.Refusal);
        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, refused.Reason);
        Assert.Equal(0, refused.ExpectedBaseVersion);
        Assert.Equal(0, await inner.GetActiveVersionAsync());
        Assert.Equal(PublishFixtures.Fields(1), Assert.Single((await inner.GetOpenDraftAsync())!.Changes.Edits).Fields);
        store.LoseMarker = false;
        Assert.True((await publisher.PrepareAsync(request, ContentPublishBaseline.Empty)).IsValid);
    }

    [Fact]
    public async Task RejectedCandidateCarriesTheCompleteExtensibleFindingSetAndWritesNothing()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore inner = PublishFixtures.Store(registry, pack);
        await PublishFixtures.ApplyAsync(inner, ContentEdit.Add(Thing, Key, PublishFixtures.Fields(1)));
        var store = new RowOnlyStoreView(inner);
        var commit = new ContentPublishCommit(store, pack, new ContentPublisher(store, inner, registry));
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            commit.PublishAsync(PublishFixtures.Request(0) with { PublishPolicy = new FindingsPolicy(true) }));

        Assert.Equal(ContentAuthoringRefusal.CandidateRejected, refused.Refusal);
        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.Reason);
        Assert.Equal(new[] { "KEC0000", "consumer-a", "consumer-b" },
            refused.Findings.Select(f => f.Code).Order(StringComparer.Ordinal));
        Assert.All(refused.Findings, finding =>
        {
            Assert.Equal(finding.Code == "KEC0000" ? default : Thing, finding.Type);
            Assert.Equal(finding.Code == "KEC0000" ? 0 : 1, finding.Id);
        });
        Assert.Equal(0, await inner.GetActiveVersionAsync());
        Assert.Null(await pack.GetVersionPointerAsync(1));
        Assert.False(System.IO.Directory.Exists(root.Path) && System.IO.Directory.GetFiles(root.Path, "*", System.IO.SearchOption.AllDirectories).Length > 0);
        Assert.Equal(PublishFixtures.Fields(1), Assert.Single((await inner.GetOpenDraftAsync())!.Changes.Edits).Fields);
        Assert.Equal(1, (await commit.PublishAsync(PublishFixtures.Request(0))).VersionNumber);
    }

    [Fact]
    public async Task RulePrefixRefusalsNameTheShortageOrMismatchingSequence()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublishPlan plan = await PublishFixtures.PublishAsync(store, PublishFixtures.Publisher(store, registry),
            Baseline(), ContentEdit.Retire(Thing, 1, Key, ContentRetirePolicy.Placeholder, 0));
        RemapRule rule = Assert.Single(plan.Rules);
        ContentAuthoringException shorter = Assert.Throws<ContentAuthoringException>(() =>
            ContentRulePrefix.Require([rule, rule], plan));
        Assert.Equal(ContentAuthoringRefusal.RulePrefixShortened, shorter.Refusal);
        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, shorter.Reason);
        Assert.Equal(1L, shorter.ActualCount);
        Assert.Equal(2L, shorter.MinimumCount);
        ContentRulePrefix.Require([rule], plan);

        var different = new RemapRule(rule.Sequence, rule.IntroducedIn, rule.Type, rule.Kind, rule.FromId, rule.ToId,
            new byte[] { RemapRule.RetirePolicyReplacement, 1, 0, 0, 0 });
        ContentAuthoringException mismatch = Assert.Throws<ContentAuthoringException>(() =>
            ContentRulePrefix.Require([different], plan));
        Assert.Equal(ContentAuthoringRefusal.RulePrefixEntryMismatch, mismatch.Refusal);
        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, mismatch.Reason);
        Assert.Equal(1, mismatch.RuleSequence);
        ContentRulePrefix.Require([rule], plan);
    }

    [Fact]
    public void FindingsAreAnOwnedReadOnlySnapshotForLegacyAndTypedConstructors()
    {
        var findings = new List<ContentFinding> { new(Thing, 1, "KEC0002", "duplicate") };
        var legacy = new ContentAuthoringException("refused", Thing, 1, ContentAuthoringException.CandidateInvalidReason, findings);
        var typed = new ContentAuthoringException(ContentAuthoringRefusal.CandidateRejected,
            "refused", Thing, 1, ContentAuthoringException.CandidateInvalidReason, findings);
        findings.Clear();
        foreach (ContentAuthoringException error in new[] { legacy, typed })
        {
            Assert.Equal("KEC0002", Assert.Single(error.Findings).Code);
            if (error.Findings is IList<ContentFinding> mutable)
            {
                Assert.Throws<NotSupportedException>(() => mutable.Clear());
            }
        }
    }

    static ContentPublishBaseline Baseline() => new(1, [new ContentRowRevision(
        new ContentRow(Thing, 1, Key, 0, false, [ContentFieldValue.OfNumber(ContentFieldKind.Int, 1),
            ContentFieldValue.Absent(ContentFieldKind.Bool)]), 1, null, null)], [], [], [], 0, 0);

    sealed class FindingsPolicy(bool reject) : IContentPublishPolicy
    {
        public void Validate(ContentPublishPolicySnapshot baseline, ContentPublishPolicySnapshot candidate,
            ICollection<ContentFinding> findings)
        {
            if (!reject) return;
            findings.Add(new ContentFinding(Thing, 1, "consumer-a", "first"));
            findings.Add(new ContentFinding(Thing, 1, "consumer-b", "second"));
        }
    }

    sealed class LostFreezeStore(InMemoryContentAuthoringStore inner) : RowOnlyStoreView(inner), IContentAuthoringStore
    {
        public bool LoseMarker { get; set; } = true;
        async Task IContentAuthoringStore.FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken)
        {
            await inner.FreezeDraftAsync(baseVersion, cancellationToken);
            if (LoseMarker) await inner.ClearDraftFreezeAsync(cancellationToken);
        }
    }
}
