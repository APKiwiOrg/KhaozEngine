using System;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageSparseContextTests
{
    [Theory]
    [InlineData(100, false)]
    [InlineData(1, true)]
    public void FineSparseSurface_VisitsOnlyAcquiredCellsAndAdmitsBeforeCompilation(int maxFaces, bool refuses)
    {
        var set = new MapSurfaceSet();
        WaterFixtures.Surface(set, "floor", MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre,
            0, 0, 1, 1, (_, _) => 100);
        var fine = new MapLatticeFrame(new(1, 1000), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0);
        WaterFixtures.Surface(set, "fine", MapSurfaceRole.SupportFloor, fine, 1000, 0, 1002, 2, (_, _) => 0);
        var view = MapScopedSurfaces.CompleteView(set);
        var support = MapSupportPreparation.PrepareFoliage(view);
        long visits = 0, compiles = 0;
        var min = new MapExactXz(new(-1, 1), new(-1, 1));
        var max = new MapExactXz(new(33, 1), new(33, 1));
        MapBoundFaceContext Run() => MapFoliageCandidateSupport.Context(view, support, min, max, maxFaces, out _,
            compile: (patch, mask) => { compiles++; return MapSurfaceCompiler.Compile(patch, mask, MapSurfaceCompiler.MaxFacesPerPatch); },
            inspectCell: () => Assert.True(++visits <= 8192, "context visited cells outside its two acquired slots"));

        if (refuses)
        {
            Assert.Equal(MapFoliageStatus.CapacityExceeded, Assert.Throws<MapFoliageRefusal>(() => Run()).Status);
            Assert.Equal(0, compiles);
        }
        else
        {
            _ = Run();
            Assert.InRange(visits, 5, 8192);
            Assert.Equal(2, compiles);
        }
        Assert.InRange(visits, 1, 8192);
        Assert.Equal(2, view.Witness.Present.Count);
    }
}
