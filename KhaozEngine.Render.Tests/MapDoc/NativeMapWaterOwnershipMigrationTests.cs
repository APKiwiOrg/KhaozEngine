using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapWaterOwnershipMigrationTests
{
    [Fact]
    public void WaterHandoff_ChargesCanonicalCellsOnceWhileBothAliasesAreLive()
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(
            NativeSceneFixtures.WaterWindow(0, 10, 7) with { MotionPaddingMetres = 1 }).Scene!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100_000_000));
        using var active = admission.Enter();
        var water = MapWaterScopedCompiler.Compile(scene);
        long compiler = admission.RetainedBytes;
        using var batch = NativeMapWater.Prepare(water, new WaterLook { SwellAmplitude = 0 }, new());
        Assert.All(batch.Cells, cell => Assert.Contains(water.Cells, original => ReferenceEquals(original, cell)));
        long canonical = batch.Cells.Sum(c => 256L + c.Regions.Sum(r => 128L + 144L * r.Triangles.Count));
        // The existing batch inventory includes canonical rows. A handoff adopts their debit atomically.
        Assert.Equal(compiler + batch.RetainedBytes - canonical, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        batch.Dispose();
        water.PreparationOwnership?.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
