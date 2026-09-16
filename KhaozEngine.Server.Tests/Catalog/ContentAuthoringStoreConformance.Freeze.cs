using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Fact 25, the FREEZE half of spec 6.2: a publish holds the draft for its whole duration, and a second
/// actor's edit is refused rather than slipped into a change set the pipeline has already read.
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// FACT 25. A draft edit arriving while a publish is in flight is refused with
    /// <c>publish-in-progress</c>, and the same edit applied after the publish succeeds.
    /// <para>
    /// <b>It is driven through the step hook, which is the only way to BE inside a publish.</b> Without the
    /// freeze the late edit is accepted, version N publishes without it, and step 10 then deletes the draft
    /// it is sitting in: an edit an operator saved, that no version carries and no draft still holds. That is
    /// what makes this an edit-loss fact rather than a locking one.
    /// </para>
    /// <para>
    /// The discard is refused on the same marker and for a sharper reason: it would delete the change set the
    /// pipeline is holding a plan for, and step 10 would then commit rows for edits the draft no longer has.
    /// </para>
    /// <para>
    /// The second half is what keeps the refusal honest. A store that refused the edit forever would pass the
    /// first half, so the same edit is applied again once the publish is over and has to land.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact25_ADraftEditArrivingWhileAPublishIsInFlightIsRefused()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        // The change set the publish will read at step 1.
        await ApplyAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(22)));

        // The second actor's edit, which arrives after the pipeline has already built its candidate.
        ContentEdit late = ContentEdit.Add(Thing, new ContentKey("late"), CatalogFixtures.Fields(33));
        ContentAuthoringException? refusedEdit = null;
        ContentAuthoringException? refusedDiscard = null;
        bool frozenMidPublish = false;

        ContentPublishCommit publish = CommitWithHook(store, step =>
        {
            if (step != ContentPublishStep.AfterManifestWrite)
            {
                return;
            }

            frozenMidPublish = Frozen(store);
            refusedEdit = Refusal(() => ApplyAsync(store, late));
            refusedDiscard = Refusal(
                () => store.DiscardDraftAsync(CatalogFixtures.Actor, CatalogFixtures.Operator));
        });

        ContentPublishResult published = await publish.PublishAsync(Request(1));

        Assert.Equal(2, published.VersionNumber);
        Assert.True(frozenMidPublish, "The draft was not frozen at step 8, so nothing was holding it.");
        Assert.NotNull(refusedEdit);
        Assert.Equal(ContentAuthoringException.PublishInProgressReason, refusedEdit.Reason);
        Assert.NotNull(refusedDiscard);
        Assert.Equal(ContentAuthoringException.PublishInProgressReason, refusedDiscard.Reason);

        // The version carries the change set step 1 read and nothing else, and the draft is gone WITH the
        // publish rather than leaving the late edit behind in it.
        Assert.Null(await store.GetOpenDraftAsync());
        ContentRowPage after = await RowsAsync(store);
        Assert.Equal(new[] { 1 }, Ids(after));
        Assert.Equal(22, after.Rows[0].Fields[0].Number);

        // The freeze is released, so the same edit lands now.
        ContentPublishResult next = await PublishAsync(store, late);
        Assert.Equal(3, next.VersionNumber);
        ContentRowPage rows = await RowsAsync(store);
        Assert.Equal(new[] { 1, 2 }, Ids(rows));
        Assert.Equal(new[] { "one", "late" }, Keys(rows));
    }

    /// <summary>Whether the open draft carries a freeze marker right now, read through the seam.</summary>
    /// <param name="store">The store.</param>
    static bool Frozen(IContentAuthoringStore store)
        => Task.Run(async () =>
        {
            ContentDraft? draft = await store.GetOpenDraftAsync().ConfigureAwait(false);
            return draft is { IsFrozen: true };
        }).GetAwaiter().GetResult();
}
