using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapWaterResidencyInventoryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SettledWaterCpu_IncludesUniqueCanonicalInventoryUntilRelease(int windows)
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        using var f = new ResidencyFixture(fixture.Document, fixture.Assets);
        var a = NativeSceneFixtures.WaterWindow(0, 1.25f) with { MotionPaddingMetres = 1 };
        var b = NativeSceneFixtures.WaterWindow(.75f, 2) with { MotionPaddingMetres = 1 };
        var state = windows == 1 ? f.State(a) : f.State(a, b);
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var plans = residency.AcceptedPlans;
        var cells = plans.SelectMany(p => p.Inventory.Water.Cells)
            .Distinct<MapWaterResourceCell>(ReferenceEqualityComparer.Instance).ToArray();
        Assert.NotEmpty(cells);
        long canonical = cells.Sum(c => 256L + c.Regions.Sum(r => 128L + 144L * r.Triangles.Count));
        long source = state.Source.Data.CaptureWork.RetainedBytes + state.Extents.MetadataBytes +
            state.Closure.CapturedResources.Sum(r => (long)r.ByteLength);
        long receipts = plans.Sum(p => p.PreparationOwnership!.Bytes + p.Scene.PreparationOwnership!.Bytes +
            p.FoliageContext.PreparationOwnership!.Bytes);
        long uploads = 100L * device.Live.Count;
        long recording = 512L + 512L * plans.Sum(p => p.Resources.Count);
        Assert.Equal(source + residency.AssetCache.RetainedBytes + receipts + canonical + uploads + recording,
            residency.Lifetime.CpuBytes);
        residency.Dispose(); residency.Dispose();
        Assert.Equal(0, residency.Lifetime.CpuBytes);
    }
}
