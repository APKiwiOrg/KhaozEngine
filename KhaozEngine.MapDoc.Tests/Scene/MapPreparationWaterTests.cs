using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationWaterTests
{
    [Fact]
    public void CapacityRefusal_PreservesItsCanonicalWitnessDigest()
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(NativeSceneFixtures.WaterWindow()).Scene!;
        string geometry = "kemap/water-cell/1/grid-origin-0-0/half-open/x-slab-fan/40000000/tiles/" +
            System.BitConverter.SingleToInt32Bits(scene.Source.Data.TileSize).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        string expected = KhaozEngine.MapDoc.MapCanonical.HashHex(w =>
        { w.WriteStartArray(); w.WriteStringValue(geometry); w.WriteEndArray(); });
        using var active = new MapSceneAdmission(new(MaxPreparedBytes: 0)).Enter();
        var result = MapWaterScopedCompiler.Compile(scene);
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Equal(expected, result.AcquiredCellWitness.ScopeDigest);
    }

    [Fact]
    public void CanonicalWater_RetainsCellsAndReleasesAllCompilerWorkspace()
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(NativeSceneFixtures.WaterWindow()).Scene!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100_000_000));
        using var active = admission.Enter();
        var water = MapWaterScopedCompiler.Compile(scene);
        Assert.Equal(MapSceneStatus.CompleteForScope, water.Status);
        Assert.NotEmpty(water.Cells);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.True(water.PreparationOwnership!.Bytes > 0);
        Assert.Equal(water.PreparationOwnership.Bytes, admission.RetainedBytes);
        water.PreparationOwnership.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
