using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The slot cache on its own, with no device and no scene: which light owns which atlas row, which rows have to be
/// re-rendered, and what happens when more lights ask than there are rows.
/// <para>
/// This is the piece the whole static budget rests on. A cache that handed the same row to two keys would draw one
/// light's casters into the other's map, and a cache that reported a row clean before anything had rendered into it
/// would let a receiver sample uninitialized memory, so both are pinned here rather than inferred from a picture.
/// </para>
/// </summary>
public sealed class PointShadowSlotsTests
{
    const long A = 11, B = 22, C = 33, D = 44;

    static PointShadowSlots TwoRows() => new(2);

    [Fact]
    public void AFirstStaticAcquireTakesRowZeroAndIsDirty()
    {
        PointShadowSlots slots = TwoRows();

        int slot = slots.Acquire(A, LightShadowMode.Static, frame: 1);

        Assert.Equal(0, slot);
        Assert.True(slots.IsDirty(slot));
        Assert.False(slots.EverRendered(slot));
        Assert.Equal(A, slots.KeyOf(slot));
        Assert.Equal(1, slots.InUse);
        Assert.Equal(2, slots.Capacity);
    }

    [Fact]
    public void AStaticKeptCleanAcrossAFrameIsNotReRendered()
    {
        PointShadowSlots slots = TwoRows();
        int first = slots.Acquire(A, LightShadowMode.Static, frame: 1);
        slots.MarkClean(first, frame: 1);

        int second = slots.Acquire(A, LightShadowMode.Static, frame: 2);

        Assert.Equal(first, second);
        Assert.False(slots.IsDirty(second));
        Assert.True(slots.EverRendered(second));
    }

    [Fact]
    public void ASecondKeyTakesTheSecondRow()
    {
        PointShadowSlots slots = TwoRows();
        slots.Acquire(A, LightShadowMode.Static, frame: 1);

        int b = slots.Acquire(B, LightShadowMode.Static, frame: 2);

        Assert.Equal(1, b);
        Assert.Equal(B, slots.KeyOf(b));
        Assert.Equal(2, slots.InUse);
    }

    [Fact]
    public void AThirdKeyEvictsTheLeastRecentlyRequestedRowAndLeavesTheRequestedOneAlone()
    {
        PointShadowSlots slots = TwoRows();
        int a = slots.Acquire(A, LightShadowMode.Static, frame: 1);
        slots.MarkClean(a, frame: 1);
        int b = slots.Acquire(B, LightShadowMode.Static, frame: 2);
        slots.MarkClean(b, frame: 2);

        // A is asked for again on frame 3, so the only evictable row is B's.
        Assert.Equal(a, slots.Acquire(A, LightShadowMode.Static, frame: 3));
        int c = slots.Acquire(C, LightShadowMode.Static, frame: 3);

        Assert.Equal(b, c);
        Assert.Equal(C, slots.KeyOf(c));
        Assert.True(slots.IsDirty(c));
        // An evicted row's contents are somebody else's, so the new owner must not be reported as rendered.
        Assert.False(slots.EverRendered(c));
        // And A kept its row, its key and its clean map.
        Assert.Equal(A, slots.KeyOf(a));
        Assert.False(slots.IsDirty(a));
    }

    [Fact]
    public void ADynamicAcquireIsDirtyOnEveryFrame()
    {
        PointShadowSlots slots = TwoRows();

        int first = slots.Acquire(D, LightShadowMode.Dynamic, frame: 1);
        Assert.True(slots.IsDirty(first));
        slots.MarkClean(first, frame: 1);
        Assert.False(slots.IsDirty(first));

        int second = slots.Acquire(D, LightShadowMode.Dynamic, frame: 2);

        Assert.Equal(first, second);
        Assert.True(slots.IsDirty(second));
        // It HAS rendered before, which is what lets the frame keep sampling its row while a budget defers the
        // rebuild. Dirty and never-rendered are two different questions.
        Assert.True(slots.EverRendered(second));
    }

    [Fact]
    public void AKeyAndAModeTogetherIdentifyARow()
    {
        PointShadowSlots slots = TwoRows();

        // A dynamic light carries no key of its own, so the scene keys it by its place in the light queue. That
        // number can collide with a static caller's key, and the two must still be two rows.
        int stat = slots.Acquire(0L, LightShadowMode.Static, frame: 1);
        int dyn = slots.Acquire(0L, LightShadowMode.Dynamic, frame: 1);

        Assert.NotEqual(stat, dyn);
        Assert.Equal(2, slots.InUse);
    }

    [Fact]
    public void AKeyWithEveryRowAlreadyRequestedThisFrameIsRefused()
    {
        PointShadowSlots slots = TwoRows();
        slots.Acquire(A, LightShadowMode.Static, frame: 4);
        slots.Acquire(B, LightShadowMode.Static, frame: 4);

        Assert.Equal(-1, slots.Acquire(C, LightShadowMode.Static, frame: 4));
        // A refusal changes nothing: the two rows keep their owners.
        Assert.Equal(A, slots.KeyOf(0));
        Assert.Equal(B, slots.KeyOf(1));
    }

    [Fact]
    public void ReleaseUnrequestedFreesTheRowsThisFrameDidNotAskFor()
    {
        PointShadowSlots slots = TwoRows();
        int a = slots.Acquire(A, LightShadowMode.Static, frame: 1);
        int b = slots.Acquire(B, LightShadowMode.Static, frame: 1);
        slots.MarkClean(a, frame: 1);
        slots.MarkClean(b, frame: 1);

        slots.Acquire(A, LightShadowMode.Static, frame: 2);
        slots.ReleaseUnrequested(frame: 2);

        Assert.Equal(1, slots.InUse);
        Assert.Equal(A, slots.KeyOf(a));
        // B's row is free, so the next key takes it rather than evicting anybody.
        Assert.Equal(b, slots.Acquire(C, LightShadowMode.Static, frame: 2));
    }

    [Fact]
    public void AFreedRowComesBackDirtyAndUnrendered()
    {
        PointShadowSlots slots = TwoRows();
        int a = slots.Acquire(A, LightShadowMode.Static, frame: 1);
        slots.MarkClean(a, frame: 1);
        slots.ReleaseUnrequested(frame: 2);

        int again = slots.Acquire(A, LightShadowMode.Static, frame: 2);

        Assert.Equal(a, again);
        Assert.True(slots.IsDirty(again));
        Assert.False(slots.EverRendered(again));
    }

    [Fact]
    public void TheOldestRenderedRowIsTheOneToRebuildFirst()
    {
        PointShadowSlots slots = TwoRows();
        int a = slots.Acquire(A, LightShadowMode.Static, frame: 1);
        slots.MarkClean(a, frame: 1);
        int b = slots.Acquire(B, LightShadowMode.Static, frame: 2);
        slots.MarkClean(b, frame: 7);

        Assert.Equal(1, slots.LastRenderedFrame(a));
        Assert.Equal(7, slots.LastRenderedFrame(b));
        // A row nobody has rendered sorts before every rendered one, because it has nothing to sample at all.
        slots.ReleaseUnrequested(frame: 8);
        int c = slots.Acquire(C, LightShadowMode.Static, frame: 8);
        Assert.Equal(int.MinValue, slots.LastRenderedFrame(c));
    }

    [Fact]
    public void MarkDirtyForcesARebuildOfAnAlreadyCleanRow()
    {
        PointShadowSlots slots = TwoRows();
        int a = slots.Acquire(A, LightShadowMode.Static, frame: 1);
        slots.MarkClean(a, frame: 1);

        slots.MarkDirty(a);

        Assert.True(slots.IsDirty(a));
        Assert.True(slots.EverRendered(a));
    }

    /// <summary>
    /// A NEW ATLAS TEXTURE IS NOT A STALE ROW, IT IS AN EMPTY ONE. Reshaping the atlas frees the texture every
    /// row lived in, so a row left reading <see cref="PointShadowSlots.EverRendered"/> would hand its light a slot
    /// pointing into freshly allocated memory and the receiver would sample it. Dirty alone does not say that by
    /// design, which is exactly why the reconfigure path needs this second verb.
    /// </summary>
    [Fact]
    public void InvalidateEveryRowKeepsTheOwnersAndForgetsTheContents()
    {
        PointShadowSlots slots = TwoRows();
        int a = slots.Acquire(A, LightShadowMode.Static, frame: 1);
        int b = slots.Acquire(B, LightShadowMode.Static, frame: 1);
        slots.MarkClean(a, frame: 1);
        slots.MarkClean(b, frame: 1);

        slots.InvalidateEveryRow();

        Assert.True(slots.IsDirty(a));
        Assert.True(slots.IsDirty(b));
        Assert.False(slots.EverRendered(a));
        Assert.False(slots.EverRendered(b));
        // The owners are untouched, so the same lights keep the same rows and nothing is evicted by a reshape.
        Assert.Equal(A, slots.KeyOf(a));
        Assert.Equal(B, slots.KeyOf(b));
        Assert.Equal(2, slots.InUse);
        Assert.Equal(a, slots.Acquire(A, LightShadowMode.Static, frame: 2));
    }

    [Fact]
    public void AnEmptyCacheAnswersEveryQuestionWithoutThrowing()
    {
        // A zero-row cache is what a scene holds before its first atlas exists, and an out-of-range row is what a
        // caller asks about after a settings change shrank the atlas under it.
        var slots = new PointShadowSlots(0);

        Assert.Equal(0, slots.Capacity);
        Assert.Equal(0, slots.InUse);
        Assert.Equal(-1, slots.Acquire(A, LightShadowMode.Static, frame: 1));
        Assert.False(slots.IsDirty(0));
        Assert.False(slots.EverRendered(-3));
        Assert.Equal(0L, slots.KeyOf(9));
        slots.MarkDirty(4);
        slots.MarkClean(4, frame: 1);
        slots.ReleaseUnrequested(frame: 1);
    }
}
