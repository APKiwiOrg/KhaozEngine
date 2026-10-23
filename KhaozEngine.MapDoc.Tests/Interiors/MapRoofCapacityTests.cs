using System;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapRoofCapacityTests
{
    [Theory]
    [InlineData(false, "MaxPreparedBytes")]
    [InlineData(true, "MaxPreparedFaces")]
    public void RoofPreparation_ReportsFailedSceneReservation(bool faces, string name)
    {
        var fixture = PrefabFixtures.LinkedBuildings();
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-32, -32), new(32, 32), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-32, -32, -32, 32, 32, 32), MapSceneChannels.All);
        var scene = NativeSceneFixtures.Open(native, false).Acquire(scope).Scene!;
        var limits = faces ? new MapSceneLimits(MaxPreparedFaces: 0) : new MapSceneLimits(MaxPreparedBytes: 0);
        var admission = new MapSceneAdmission(limits);
        var result = MapRoofEvaluationBuffer.Prepare(scene, admission);
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Null(result.Buffer);
        Assert.Equal(name, result.Capacity!.LimitName);
        Assert.Equal(0, result.Capacity.Allowance);
        Assert.True(result.Capacity.Requested > 0);
        Assert.Contains(name, result.Detail);
        Assert.Contains("allowance 0", result.Detail);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.PreparedFaces);
    }

    [Fact]
    public void RoofPreparation_ReportsActivePlanWindowAllowance()
    {
        var fixture = PrefabFixtures.LinkedBuildings();
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-32, -32), new(32, 32), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-32, -32, -32, 32, 32, 32), MapSceneChannels.All);
        var scene = NativeSceneFixtures.Open(native, false).Acquire(scope).Scene!;
        var admission = new MapSceneAdmission(new());
        admission.ReserveBytes(20);
        using var window = admission.LimitPreparation(1, "MaxPlanBytes", 2);
        var result = MapRoofEvaluationBuffer.Prepare(scene, admission);
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Equal("MaxPlanBytes", result.Capacity!.LimitName);
        Assert.Equal(3, result.Capacity.Allowance);
        Assert.True(result.Capacity.Requested > 3);
        Assert.Contains("MaxPlanBytes", result.Detail);
        Assert.Contains("allowance 3", result.Detail);
        Assert.Equal(20, admission.RetainedBytes);
    }
}
