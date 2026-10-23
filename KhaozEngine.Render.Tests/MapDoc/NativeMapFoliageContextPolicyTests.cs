using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapFoliageContextPolicyTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(134217728, true)]
    public void Residency_EnforcesDeclaredFoliageContextWithSharedMemory(long sourceBytes, bool accepts)
    {
        using var fixture = ResidencyFixture.Foliage(4);
        var original = fixture.FoliageState();
        var state = fixture.StateWithLimits(new(MaxPreparedBytes: 134_217_728)
        { Preparation = new(FoliageContext: new(MaxSourceUtf8Bytes: sourceBytes)) }, original.Windows.ToArray())
            with { Policy = original.Policy };
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(),
            new(MaxQueuedBytes: 134_217_728, MaxPlanBytes: 134_217_728, MaxResidentCpuBytes: 536_870_912), scheduler);
        if (accepts)
        {
            ResidencyFixture.Complete(residency, scheduler);
            Assert.Single(residency.AcceptedPlans);
        }
        else
        {
            scheduler.RunAll(); residency.Update(); residency.Update();
            var transition = residency.InspectReadiness().Transition;
            Assert.Equal(MapSceneStatus.CapacityExceeded, transition.Status);
            Assert.Equal("MaxSourceUtf8Bytes", transition.Capacity!.LimitName);
            Assert.Equal(1, transition.Capacity.Allowance);
            Assert.True(transition.Capacity.Requested > 1);
            Assert.Empty(residency.AcceptedPlans);
        }
    }

}
