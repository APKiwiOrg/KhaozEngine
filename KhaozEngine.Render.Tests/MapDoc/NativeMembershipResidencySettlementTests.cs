using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMembershipResidencySettlementTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ActiveWindows_ChargeExactUniqueRootInventory(int windows)
    {
        using var native = MembershipAdmissionFixtures.Create();
        using var fixture = new ResidencyFixture(native.Document, native.Assets);
        var a = NativePresentationFixtures.Scope;
        var b = a with { MotionPaddingMetres = 1 };
        var state = fixture.State(windows == 1 ? [a] : [a, b]);
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), new(MaxPlanBytes: 67108864), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(windows, residency.AcceptedPlans.Count);
        Assert.Equal(windows - 1, residency.AcceptedPlans.Sum(p => p.RoofEvaluation.Counts.FootprintCacheHits));
        Assert.Equal(ActiveCpu(state, residency), residency.Lifetime.CpuBytes);
        residency.SetWindows([]);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(SourceCpu(state), residency.Lifetime.CpuBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnloadEitherWindow_LeavesExactRemainingObligation(bool unloadFirst)
    {
        using var native = MembershipAdmissionFixtures.Create();
        using var fixture = new ResidencyFixture(native.Document, native.Assets);
        var a = NativePresentationFixtures.Scope;
        var b = a with { MotionPaddingMetres = 1 };
        var state = fixture.State(a, b);
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), new(MaxPlanBytes: 67108864), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        residency.SetWindows([unloadFirst ? b : a]);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Single(residency.AcceptedPlans);
        Assert.Equal(ActiveCpu(state, residency), residency.Lifetime.CpuBytes);
        residency.SetWindows([]);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(SourceCpu(state), residency.Lifetime.CpuBytes);
        Assert.Equal(0, residency.Lifetime.GpuBytes);
    }

    [Fact]
    public void RecenterWindow_SettlesTheNewUniqueUnion()
    {
        using var native = MembershipAdmissionFixtures.Create();
        using var fixture = new ResidencyFixture(native.Document, native.Assets);
        var a = NativePresentationFixtures.Scope;
        var state = fixture.State(a, a with { MotionPaddingMetres = 1 });
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), new(MaxPlanBytes: 67108864), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        residency.SetWindows([a, a with { DrawBounds = a.DrawBounds with { MinX = -15, MaxX = 17 } }]);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(2, residency.AcceptedPlans.Count);
        Assert.Equal(ActiveCpu(state, residency), residency.Lifetime.CpuBytes);
        residency.SetWindows([]);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(SourceCpu(state), residency.Lifetime.CpuBytes);
    }

    static long SourceCpu(MapRenderSourceState state) => state.Source.Data.CaptureWork.RetainedBytes +
        state.Extents.MetadataBytes + state.Closure.CapturedResources.Sum(r => (long)r.ByteLength);

    static long ActiveCpu(MapRenderSourceState state, MapRenderResidency residency) => SourceCpu(state) + 512 +
        NativeMembershipResidencyInventory.Preparation(residency.AcceptedPlans, MembershipAdmissionFixtures.Cells);
}
