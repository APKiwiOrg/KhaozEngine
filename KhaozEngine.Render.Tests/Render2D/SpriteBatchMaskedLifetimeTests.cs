using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using static KhaozEngine.Tests.Render2D.SpriteBatchMaskedTestRig;

namespace KhaozEngine.Tests.Render2D;

public sealed class SpriteBatchMaskedLifetimeTests
{
    static readonly Vector4 Dest = new(0, 0, 4, 4);

    static void Draw(SpriteBatch batch, Texture2D source, Texture2D mask) =>
        batch.DrawMasked(source, Dest, mask, Color.White);

    static FakeResourceSet MaskedSet(SpriteBatchMaskedTestRig rig, Texture2D source, Texture2D mask) =>
        rig.Factory.ResourceSets.Last(set => set.Resources.Length == 4 &&
            ReferenceEquals(set.Resources[0], source.Handle) && ReferenceEquals(set.Resources[2], mask.Handle));

    [Fact]
    public void SharedSourceDoesNotKeepOldMasksAlive()
    {
        using var rig = Create();
        using var source = Sprite();
        using var maskA = Sprite();
        using var maskB = Sprite();
        var batch = rig.Batch;
        var device = rig.InnerDevice;
        batch.SetEvictAfterFrames = 2;
        rig.Frame(b => { Draw(b, source, maskA); Draw(b, source, maskB); });
        var setA = MaskedSet(rig, source, maskA);
        var setB = MaskedSet(rig, source, maskB);

        rig.Frame(b => Draw(b, source, maskB)); // frame 2
        Assert.Equal(2, batch.CachedMaskedSetCount);
        rig.Frame(b => Draw(b, source, maskB)); // frame 3

        Assert.Equal(1, batch.CachedMaskedSetCount); // frame 3
        Assert.Equal(0, batch.CachedSetCount);
        Assert.False(setA.Disposed);
        Assert.Equal(0, device.WaitForIdleCalls);
        Assert.False(((FakeTexture)source.Handle).Disposed);
        Assert.False(((FakeTexture)maskA.Handle).Disposed);
        rig.Frame(b => Draw(b, source, maskB)); // frame 4
        Assert.False(setA.Disposed);
        rig.Frame(b => Draw(b, source, maskB)); // frame 5
        Assert.False(setA.Disposed);
        rig.Frame(b => Draw(b, source, maskB)); // frame 6
        Assert.False(setA.Disposed);
        rig.Frame(b => Draw(b, source, maskB)); // frame 7
        Assert.True(setA.Disposed);
        Assert.Equal(1, setA.DisposeCalls);
        Assert.False(setB.Disposed);
        Assert.Equal(0, device.WaitForIdleCalls);
    }

    [Fact]
    public void SharedMaskDoesNotKeepOldSourcesAlive()
    {
        using var rig = Create();
        using var sourceA = Sprite();
        using var sourceB = Sprite();
        using var mask = Sprite();
        rig.Batch.SetEvictAfterFrames = 2;
        rig.Frame(b => { Draw(b, sourceA, mask); Draw(b, sourceB, mask); });
        var setA = MaskedSet(rig, sourceA, mask);
        rig.Frame(b => Draw(b, sourceB, mask));
        Assert.Equal(2, rig.Batch.CachedMaskedSetCount);
        rig.Frame(b => Draw(b, sourceB, mask));

        Assert.Equal(1, rig.Batch.CachedMaskedSetCount);
        Assert.False(setA.Disposed);
        Assert.Equal(0, rig.InnerDevice.WaitForIdleCalls);
        rig.Frame(b => Draw(b, sourceB, mask)); // frame 4
        Assert.False(setA.Disposed);
        rig.Frame(b => Draw(b, sourceB, mask)); // frame 5
        Assert.False(setA.Disposed);
        rig.Frame(b => Draw(b, sourceB, mask)); // frame 6
        Assert.False(setA.Disposed);
        rig.Frame(b => Draw(b, sourceB, mask)); // frame 7
        Assert.Equal(1, setA.DisposeCalls);
        Assert.False(((FakeTexture)sourceA.Handle).Disposed);
        Assert.False(((FakeTexture)mask.Handle).Disposed);
        Assert.Equal(0, rig.InnerDevice.WaitForIdleCalls);
    }

    [Fact]
    public void GroupedKeysAreForgottenOnEviction()
    {
        using var rig = Create();
        using var source = Sprite();
        using var maskA = Sprite();
        using var maskB = Sprite();
        rig.Batch.SetEvictAfterFrames = 2;
        rig.Frame(b =>
        {
            Draw(b, source, maskA);
            b.BlendMode = BlendMode.Additive;
            Draw(b, source, maskA);
            Draw(b, source, maskB);
        }, group: true);
        var owner = rig.MaskedOwner!;
        var sampler = rig.Device.LinearSampler;
        var alphaA = owner.KeyFor(source.Handle, sampler, maskA.Handle, sampler, BlendMode.Alpha, 1);
        var additiveA = owner.KeyFor(source.Handle, sampler, maskA.Handle, sampler, BlendMode.Additive, 1);
        var additiveB = owner.KeyFor(source.Handle, sampler, maskB.Handle, sampler, BlendMode.Additive, 1);
        Assert.True(rig.HasGroup(alphaA));
        Assert.True(rig.HasGroup(additiveA));

        rig.Frame(b => Draw(b, source, maskB)); // leave the old group order intact
        using (GpuRecording.Open(rig.Device, rig.Commands, "pending ranges before eviction"))
        {
            rig.Batch.Begin();
            Draw(rig.Batch, source, maskB);
            // The next frame abandons these vertices before eviction forgets A's keys.
        }
        rig.Frame(b => Draw(b, source, maskB));

        Assert.Equal(1, rig.Batch.CachedMaskedSetCount);
        Assert.False(rig.HasGroup(alphaA));
        Assert.False(rig.HasGroup(additiveA));
        Assert.DoesNotContain(alphaA, rig.GroupOrder);
        Assert.DoesNotContain(additiveA, rig.GroupOrder);
        Assert.True(rig.HasGroup(additiveB));
    }

    [Fact]
    public void ReturningTupleRebuildsOnce()
    {
        using var rig = Create();
        using var source = Sprite();
        using var maskA = Sprite();
        using var maskB = Sprite();
        rig.Batch.SetEvictAfterFrames = 2;
        rig.Frame(b => { Draw(b, source, maskA); Draw(b, source, maskB); });
        var oldA = MaskedSet(rig, source, maskA);
        rig.Frame(b => Draw(b, source, maskB));
        rig.Frame(b => Draw(b, source, maskB));
        Assert.Equal(1, rig.Batch.CachedMaskedSetCount);
        int created = rig.Factory.ResourceSets.Count;
        rig.Frame(b => { Draw(b, source, maskA); Draw(b, source, maskB); }); // frame 4
        var newA = MaskedSet(rig, source, maskA);
        Assert.NotSame(oldA, newA);
        Assert.Equal(created + 1, rig.Factory.ResourceSets.Count);
        rig.Frame(b => { Draw(b, source, maskA); Draw(b, source, maskB); }); // frame 5
        rig.Frame(b => { Draw(b, source, maskA); Draw(b, source, maskB); }); // frame 6
        Assert.False(oldA.Disposed);
        rig.Frame(b => { Draw(b, source, maskA); Draw(b, source, maskB); }); // frame 7

        Assert.Equal(created + 1, rig.Factory.ResourceSets.Count);
        Assert.Equal(1, oldA.DisposeCalls);
        Assert.Same(newA, MaskedSet(rig, source, maskA));
        Assert.False(newA.Disposed);
        Assert.Equal(2, rig.Batch.CachedMaskedSetCount);
        Assert.Equal(0, rig.InnerDevice.WaitForIdleCalls);
    }

    [Fact]
    public void AliasedSourceAndMaskAreNeverOwned()
    {
        using var rig = Create();
        using var shared = Sprite();
        using var other = Sprite();
        rig.Batch.SetEvictAfterFrames = 2;
        rig.Frame(b =>
        {
            Draw(b, shared, shared);
            Draw(b, shared, other);
            Draw(b, other, shared);
            b.Draw(shared, Vector2.Zero, Color.White);
        });
        var aliasSet = MaskedSet(rig, shared, shared);
        rig.Frame();
        rig.Frame();
        Assert.Equal(0, rig.Batch.CachedMaskedSetCount);
        Assert.Equal(0, rig.Batch.CachedSetCount);
        Assert.False(aliasSet.Disposed);

        rig.Batch.Dispose();

        Assert.True(aliasSet.Disposed);
        Assert.False(((FakeTexture)shared.Handle).Disposed);
        Assert.False(((FakeTexture)other.Handle).Disposed);
        Assert.False(Assert.IsType<TrackedSampler>(rig.Device.LinearSampler).Disposed);
        Assert.False(Assert.IsType<TrackedSampler>(rig.Device.PointSampler).Disposed);
    }

    [Fact]
    public void DisposeUsesExistingDrainAndAggregatesFailures()
    {
        using var rig = Create();
        using var source = Sprite();
        using var maskA = Sprite();
        using var maskB = Sprite();
        rig.Batch.SetEvictAfterFrames = 2;
        rig.Frame(b => { Draw(b, source, maskA); Draw(b, source, maskB); }, group: true);
        var owner = rig.MaskedOwner!;
        var sampler = rig.Device.LinearSampler;
        var alphaB = owner.KeyFor(source.Handle, sampler, maskB.Handle, sampler, BlendMode.Alpha, 1);
        var retired = MaskedSet(rig, source, maskA);
        var live = MaskedSet(rig, source, maskB);
        rig.Frame(b => Draw(b, source, maskB));
        rig.Frame(b => Draw(b, source, maskB));
        var descriptorFailure = new InvalidOperationException("planned masked descriptor disposal failure");
        var layoutFailure = new InvalidOperationException("planned masked layout disposal failure");
        live.OnDispose = () => throw descriptorFailure;
        var maskedLayout = rig.Forwarder.Layouts[2];
        maskedLayout.DisposalFailure = layoutFailure;
        rig.InnerDevice.OnWaitForIdle = () =>
        {
            Assert.False(retired.Disposed);
            Assert.False(live.Disposed);
            Assert.False(maskedLayout.Disposed);
        };
        maskedLayout.OnDispose = () =>
        {
            Assert.Equal(1, rig.InnerDevice.WaitForIdleCalls);
            Assert.True(retired.Disposed);
            Assert.False(rig.Factory.Buffers[0].Disposed);
            Assert.False(rig.Forwarder.Layouts[1].Disposed);
        };

        var error = Assert.Throws<GpuDiagnosticFailureException>(rig.Batch.Dispose);

        Assert.Equal(new Exception[] { descriptorFailure, layoutFailure }, error.Failures.Select(f => f.Error));
        Assert.All(error.Failures, failure => Assert.StartsWith("renderer.session.dispose.ui.masked.", failure.Stage));
        Assert.Equal(1, retired.DisposeCalls);
        Assert.Equal(1, live.DisposeCalls);
        Assert.All(rig.Factory.ResourceSets.Where(set => !ReferenceEquals(set, live)), set => Assert.True(set.Disposed));
        Assert.All(rig.Factory.Buffers, buffer => Assert.True(buffer.Disposed));
        Assert.All(rig.Factory.Pipelines, pipeline => Assert.Equal(1, pipeline.DisposeCount));
        Assert.All(rig.Factory.ShaderSets, shaders => Assert.Equal(1, shaders.DisposeCount));
        Assert.All(rig.Forwarder.Layouts, layout => Assert.True(layout.Disposed));
        Assert.Null(rig.MaskedOwner);
        Assert.Empty(rig.GroupOrder);
        Assert.False(rig.HasGroup(alphaB));
        Assert.Equal(0, owner.CachedSetCount);
        Assert.Equal(0, rig.Batch.CachedMaskedSetCount);
        rig.Batch.Dispose();
        Assert.Equal(1, rig.InnerDevice.WaitForIdleCalls);
        Assert.Equal(1, live.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeDuringRecordingStopsBeforeMaskedRelease(bool pendingRetirement)
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        rig.Batch.SetEvictAfterFrames = 2;
        rig.Frame(b => Draw(b, source, mask));
        var set = MaskedSet(rig, source, mask);
        if (pendingRetirement) { rig.Frame(); rig.Frame(); }
        using (GpuRecording.Open(rig.Device, rig.Commands, "masked disposal barrier"))
        {
            var error = Assert.Throws<GpuDiagnosticFailureException>(rig.Batch.Dispose);
            Assert.IsType<GpuDrainDuringRecordingException>(Assert.Single(error.Failures).Error);
            Assert.False(rig.Batch.DisposalSafeForDependants);
            Assert.False(set.Disposed);
            Assert.All(rig.Forwarder.Layouts, layout => Assert.False(layout.Disposed));
            Assert.All(rig.Factory.Pipelines, pipeline => Assert.False(pipeline.Disposed));
            Assert.All(rig.Factory.ShaderSets, shaders => Assert.False(shaders.Disposed));
            Assert.Equal(0, rig.InnerDevice.WaitForIdleCalls);
        }

        rig.Batch.Dispose();
        Assert.True(set.Disposed);
        Assert.Equal(1, rig.InnerDevice.WaitForIdleCalls);
    }

    [Fact]
    public void KeyOnlyEntriesAgeByTheirOwnLastUse()
    {
        using var rig = Create();
        using var source = Sprite();
        using var maskA = Sprite();
        using var maskB = Sprite();
        var owner = rig.EnsureMaskedResources();
        var sampler = rig.Device.LinearSampler;
        var a = owner.KeyFor(source.Handle, sampler, maskA.Handle, sampler, BlendMode.Alpha, 1);
        var b = owner.KeyFor(source.Handle, sampler, maskB.Handle, sampler, BlendMode.Alpha, 1);
        Assert.Same(b, owner.KeyFor(source.Handle, sampler, maskB.Handle, sampler, BlendMode.Alpha, 2));
        var forgotten = new List<object>();
        var retired = new List<IGpuResourceSet>();
        owner.Evict(3, 2, forgotten.Add, retired.Add);

        Assert.Equal(new object[] { a, a.Entry.Additive }, forgotten);
        Assert.Empty(retired);
        Assert.Equal(0, owner.CachedSetCount);
        Assert.Same(b, owner.KeyFor(source.Handle, sampler, maskB.Handle, sampler, BlendMode.Alpha, 3));
        Assert.NotSame(a, owner.KeyFor(source.Handle, sampler, maskA.Handle, sampler, BlendMode.Alpha, 3));
    }
}
