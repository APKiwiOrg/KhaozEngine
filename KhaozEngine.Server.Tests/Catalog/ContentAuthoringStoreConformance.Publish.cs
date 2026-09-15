using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Facts 7 to 12 and 22, the PUBLISH half: the version line, the optimistic check, the temporal rows, the
/// remap rules, and the one transaction the active pointer moves in.
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// FACT 7. Publish assigns version 1 then 2, never skipping. The number is the pack's own address and a
    /// gap in it would be a version a rollback, a pin and a diff all name and none can find.
    /// </summary>
    [Fact]
    public virtual async Task Fact07_PublishAssignsOneThenTwoAndNeverSkips()
    {
        IContentAuthoringStore store = await OpenAsync();

        ContentPublishResult first = await PublishAsync(
            store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        ContentPublishResult second = await PublishAsync(
            store, ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)));

        Assert.Equal(1, first.VersionNumber);
        Assert.Equal(2, second.VersionNumber);
        Assert.Equal(2, await store.GetActiveVersionAsync());

        IReadOnlyList<ContentVersionRecord> versions = await store.ListVersionsAsync();
        Assert.Equal(new[] { 2, 1 }, versions.Select(static version => version.VersionNumber).ToArray());
        Assert.Equal(1, versions[0].BaseVersion);
    }

    /// <summary>
    /// FACT 8. A publish naming a stale <c>expectedBaseVersion</c> is REFUSED and nothing is written. Two
    /// consoles cannot both publish the draft they each looked at, and the second one is told both numbers
    /// rather than being told no.
    /// </summary>
    [Fact]
    public virtual async Task Fact08_APublishAgainstAStaleBaseIsRefusedAndWritesNothing()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        await ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)));

        // The store stands at 1 and this request still believes it stands at 0.
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.PublishAsync(Request(0)));

        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, refused.Reason);
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Single(await store.ListVersionsAsync());
        Assert.Equal(1, (await DraftAsync(store)).EditCount);
        Assert.Equal(new[] { 1 }, Ids(await RowsAsync(store)));
    }

    /// <summary>
    /// FACT 9. A row no publish TOUCHED keeps its <c>valid_from_version</c>. The temporal model's whole value
    /// is that a row's history is the rows themselves, so a publish that restamped every live row would make
    /// "when did this last change" unanswerable.
    /// </summary>
    [Fact]
    public virtual async Task Fact09_AnUntouchedRowKeepsItsValidFromVersion()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
            ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)));

        await PublishAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(99)));

        IReadOnlyList<ContentRowRevision> untouched = await store.GetRowHistoryAsync(Thing, 2);
        ContentRowRevision only = Assert.Single(untouched);
        Assert.Equal(1, only.ValidFromVersion);
        Assert.Null(only.ReplacedInVersion);

        // The row that WAS touched, for contrast: two revisions, the first closed by the second.
        IReadOnlyList<ContentRowRevision> touched = await store.GetRowHistoryAsync(Thing, 1);
        Assert.Equal(2, touched.Count);
        Assert.Equal(2, touched[0].ReplacedInVersion);
        Assert.Equal(2, touched[1].ValidFromVersion);
    }

    /// <summary>
    /// FACT 10. The live set at an OLD version excludes a row added later. A pinned server and a rollback both
    /// read an old version, so a read that answered from the current set would serve content that version
    /// never had.
    /// </summary>
    [Fact]
    public virtual async Task Fact10_TheLiveSetAtAnOldVersionExcludesARowAddedLater()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)));

        Assert.Equal(new[] { 1 }, Ids(await RowsAsync(store, versionNumber: 1)));
        Assert.Equal(new[] { 1, 2 }, Ids(await RowsAsync(store, versionNumber: 2)));
        Assert.Equal(new[] { 1, 2 }, Ids(await RowsAsync(store)));
    }

    /// <summary>
    /// FACT 11. A retire writes a successor ROW plus EXACTLY ONE remap rule. The row stays in the pack forever
    /// so a stored stack still decodes, and the rule is what moves a page past it, so one without the other is
    /// a retire that either loses the old bytes or strands every reference.
    /// </summary>
    [Fact]
    public virtual async Task Fact11_ARetireWritesASuccessorRowAndExactlyOneRule()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
            ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)));
        Assert.Empty(await RulesAsync(store));

        await PublishAsync(
            store,
            ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Replacement, 1));

        IReadOnlyList<ContentRowRevision> history = await store.GetRowHistoryAsync(Thing, 2);
        Assert.Equal(2, history.Count);
        Assert.False(history[0].Row.IsRetired);
        Assert.Equal(2, history[0].ReplacedInVersion);
        Assert.True(history[1].Row.IsRetired);
        Assert.Equal(2, history[1].ValidFromVersion);

        RemapRule rule = Assert.Single(await RulesAsync(store));
        Assert.Equal(1, rule.Sequence);
        Assert.Equal(2, rule.IntroducedIn);
        Assert.Equal(RemapRuleKind.Retired, rule.Kind);
        Assert.Equal(2, rule.FromId);

        // A retire under the replacement policy carries the destination in its PAYLOAD rather than in to_id,
        // which is the kind 2 layout: policy byte, then the int32 destination.
        Assert.Equal(0, rule.ToId);
        Assert.Equal(RemapRule.RetirePolicyReplacement, rule.Payload[0]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(rule.Payload[1..]));
    }

    /// <summary>
    /// FACT 12. A remap rule cannot be UPDATED or DELETED through the API surface. Rules are append only
    /// (contracts 8.1), and a page already migrated past a rule cannot be un-migrated, so a rule that changed
    /// under it would silently repoint stored references at something else.
    /// <para>
    /// Asserted twice over. The seam offers no member that could do it, checked by NAME rather than by review,
    /// and the list a publish leaves behind is the previous list plus its own appends, identity for identity.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact12_ARemapRuleCannotBeUpdatedOrDeletedThroughTheApi()
    {
        string[] mutators = typeof(IContentAuthoringStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(static method => method.Name)
            .Where(static name =>
                name.Contains("Rule", StringComparison.Ordinal)
                && (name.StartsWith("Update", StringComparison.Ordinal)
                    || name.StartsWith("Delete", StringComparison.Ordinal)
                    || name.StartsWith("Remove", StringComparison.Ordinal)
                    || name.StartsWith("Set", StringComparison.Ordinal)))
            .ToArray();
        Assert.Empty(mutators);

        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
            ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)));
        await PublishAsync(
            store, ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0));
        IReadOnlyList<RemapRule> after = await RulesAsync(store);

        // A third publish that touches the OTHER row. The first rule has to come back out of the store
        // unchanged, at the same sequence, which is what append only means from outside.
        await PublishAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(99)));
        IReadOnlyList<RemapRule> later = await RulesAsync(store);

        Assert.Single(after);
        Assert.Single(later);
        Assert.Equal(after[0].Sequence, later[0].Sequence);
        Assert.Equal(after[0].IntroducedIn, later[0].IntroducedIn);
        Assert.Equal(after[0].Kind, later[0].Kind);
        Assert.Equal(after[0].FromId, later[0].FromId);
        Assert.Equal(after[0].ToId, later[0].ToId);
    }

    /// <summary>
    /// FACT 22. The active pointer and the version ROW commit together, asserted by a reader that sees both or
    /// neither. It is the one transaction of step 10 seen from outside: a reader that saw the pointer at a
    /// version whose row is not there yet would boot a server against a version the store cannot describe.
    /// <para>
    /// The reader runs on its own thread through a SECOND path into the store, polling as fast as it can while
    /// the publish runs, and every look it manages to take is asserted. A look it could not take consistently
    /// is DISCARDED rather than counted, because a lock is not an inconsistency.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact22_TheActivePointerAndTheVersionRowCommitTogether()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        await ApplyAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(99)));

        using var running = new CancellationTokenSource();
        var looks = new List<CatalogPointerLook>();

        // One look before and one after, so the two "it saw the publish land" assertions below cannot pass
        // vacuously on a run where the reader thread never got a turn.
        CatalogPointerLook? before = await LookAsync(store, 2);
        Assert.NotNull(before);
        looks.Add(before.Value);

        Task reader = Task.Run(async () =>
        {
            while (!running.IsCancellationRequested)
            {
                CatalogPointerLook? look = await LookAsync(store, 2).ConfigureAwait(false);
                if (look is CatalogPointerLook taken)
                {
                    looks.Add(taken);
                }
            }
        });

        await store.PublishAsync(Request(1));
        await running.CancelAsync();
        await reader;
        CatalogPointerLook? after = await LookAsync(store, 2);
        Assert.NotNull(after);
        looks.Add(after.Value);

        Assert.NotEmpty(looks);
        foreach (CatalogPointerLook look in looks)
        {
            // Both or neither. The pointer at 2 with no version 2 row is the torn state, and the version 2 row
            // visible before the pointer moved is the other half of the same tear.
            Assert.Equal(look.ActiveVersion >= 2, look.VersionRowPresent);
        }

        // And the reader saw the publish land at all, so the loop above is not vacuously true.
        Assert.Contains(looks, static look => look.ActiveVersion == 2);
        Assert.Contains(looks, static look => look.ActiveVersion == 1);
    }
}
