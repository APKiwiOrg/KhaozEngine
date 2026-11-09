using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationCallerAuditTests
{
    [Fact]
    public void PartialPaint_AdmitsRefinementAndRetainsOnlyItsPublishedRegions()
    {
        var resources = Prefabs.PrefabFixtures.Effective(Prefabs.PrefabFixtures.Create("uneven-painted"));
        var view = resources.CompleteView();
        long faces = view.Surfaces.Where(s => s.Role == MapSurfaceRole.PaintOverride)
            .Select(s => view.Witness.Present.Where(p => p.Key.SurfaceId == s.Id).Sum(p =>
            {
                Assert.True(view.TryAcquiredPatch(p.Key, out var patch, out _));
                return MapSurfaceCompiler.CountFaces(s with { Role = MapSurfaceRole.SupportFloor, PaintPriority = 0 }, patch!, MapSlotCellMask.All);
            })).Max();
        Assert.True(faces > 0);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100_000_000));
        using var active = admission.Enter();
        var result = MapPartialPaintCompiler.Compile(view, "ground");
        Assert.True(admission.PeakBytes >= 4096L * faces);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(128L + result.Regions.Sum(r => 128L + 48L * r.Polygon.Count), admission.RetainedBytes);
        result.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
    [Fact]
    public void SupportValidation_ReleasesConsumedCompiledGeometry()
    {
        var resource = Prefabs.PrefabFixtures.DeckSupport();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100_000));
        using var active = admission.Enter(); using var preparation = admission.BeginPreparation();
        MapSupportResourceFile.Require(resource);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void EffectiveWaterValidation_ReleasesConsumedBodies()
    {
        var fixture = Prefabs.PrefabFixtures.RoomWater(connected: true);
        var resources = Prefabs.PrefabFixtures.Effective(fixture);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 10_000_000));
        using var active = admission.Enter();
        KhaozEngine.MapDoc.MapBoundDocumentValidation.ValidateEffectiveWater(resources, fixture.Assets);
        Assert.True(admission.PreparedFaces > 0);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }
}
