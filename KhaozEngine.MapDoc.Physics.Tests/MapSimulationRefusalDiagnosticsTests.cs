using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapSimulationRefusalDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcquisitionRefusal_PreservesNamedCapacityAndFindings(bool preparation)
    {
        using var fixture = NativeSceneFixtures.Create("growth");
        var ready = NativeSceneFixtures.Open(fixture, false);
        var source = new MapSceneSource(ready.Data with
        {
            Limits = preparation ? new(MaxPreparedBytes: 0) : new(MaxSelectedResources: 0),
        });
        var scope = MapPhysicsPreparationFixtures.Window();
        var expected = preparation ? new MapSceneCapacity("MaxPreparedBytes", 128, 0) : new("MaxSelectedResources", 1, 0);
        using var neutral = MapRequiredDomainCoverage.Acquire(source, [scope]);
        Assert.Equal(MapSceneStatus.CapacityExceeded, neutral.Status);
        Assert.Equal(expected, neutral.Capacity);
        Assert.Contains(expected.Message, neutral.Findings);
        using var simulation = new MapSimulationCoverage(source, fixture.Assets, NativeWorldFixtures.Options()).Acquire([scope]);
        Assert.Equal(MapSceneStatus.CapacityExceeded, simulation.Status);
        Assert.Empty(simulation.Worlds);
        Assert.Equal(expected, simulation.Capacity);
        Assert.Contains(expected.Message, simulation.Findings);
    }

    [Fact]
    public void PhysicsRefusal_PreservesNamedCapacityAndFindings()
    {
        using var fixture = NativeSceneFixtures.Create("terrain-wall");
        var ready = NativeSceneFixtures.Open(fixture, false);
        var scope = MapPhysicsPreparationFixtures.Window();
        using var measured = MapRequiredDomainCoverage.Acquire(ready, [scope]);
        long faces = Assert.Single(measured.Scenes).SurfaceView.Scope.SceneAdmission!.PreparedFaces;
        var source = new MapSceneSource(ready.Data with { Limits = new(MaxPreparedFaces: checked((int)faces)) });
        using var simulation = new MapSimulationCoverage(source, fixture.Assets, NativeWorldFixtures.Options()).Acquire([scope]);
        Assert.Equal(MapSceneStatus.CapacityExceeded, simulation.Status);
        Assert.Empty(simulation.Worlds);
        var expected = new MapSceneCapacity("MaxPreparedFaces", faces + 2, faces);
        Assert.Equal(expected, simulation.Capacity);
        Assert.Contains(expected.Message, simulation.Findings);
    }
}
