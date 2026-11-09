using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapTerrainSeamAdmissionTests
{
    [Fact]
    public void SparseLargeSeam_RefusesBeforeValidationGeometry()
    {
        var (view, partitions) = Fixture(subdivided: false);
        int geometryEntries = 0;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 200_000), stage =>
        {
            if (stage == "geometry") geometryEntries++;
        });
        var refusal = Assert.Throws<MapSceneCapacityException>(() => MapTerrainPaint.Boundaries(partitions, view, admission));
        Assert.Equal("MaxPreparedBytes", refusal.Capacity.LimitName);
        // Stored corners of two 64x64 patches exceed this allowance even with only two present cells.
        Assert.True(refusal.Capacity.Requested > 200_000);
        Assert.Equal(0, geometryEntries);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulSeamValidation_ReleasesItsWorkspace(bool subdivided)
    {
        var (view, partitions) = Fixture(subdivided);
        int geometryEntries = 0;
        MapSceneAdmission? admission = null;
        admission = new(new(MaxPreparedBytes: 100_000_000), stage =>
        {
            if (stage != "geometry") return;
            geometryEntries++;
            Assert.True(admission!.WorkspaceBytes >= 2L * (4225 * 64 + 4096 * 16));
        });
        using (var preparation = admission.BeginPreparation())
        {
            var boundaries = MapTerrainPaint.Boundaries(partitions, view, admission);
            Assert.NotEmpty(boundaries);
            Assert.Equal(0, admission.WorkspaceBytes);
            // Each full patch has 4225 stored corners, before subdivision and validation edge storage.
            Assert.True(admission.PeakBytes >= 2L * (4225 * 64 + 4096 * 16));
            Assert.True(admission.PeakBytes > admission.RetainedBytes);
            Assert.Equal(1, geometryEntries);
        }
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }

    static (MapScopedSurfaces View, IReadOnlyList<MapTerrainPartition> Partitions) Fixture(bool subdivided)
    {
        using var fixture = NativeSceneFixtures.Create("terrain-feather");
        var surfaces = fixture.Document.Surfaces;
        var first = surfaces.Patches.Values.Single();
        first.CellMinX = 0; first.Width = first.Depth = 64;
        first.Heights = Enumerable.Repeat(125, 65 * 65).ToArray();
        first.Cells = Enumerable.Repeat(first.Cells[0], 64 * 64).ToArray();
        first.Presence = new ulong[64]; first.SetPresent(63, 0, true);
        var second = first.Clone();
        second.Key = new("adjoining", 1, 0);
        second.Presence = new ulong[64]; second.SetPresent(0, 0, true);
        second.Cells[0] = second.Cells[0] with { Underlay = 11, Overlay = 0, Flags = 0 };
        surfaces.Patches.Add(second.Key, second);
        surfaces.Refs.Add(surfaces.Refs[0] with { Id = "adjoining" });
        if (subdivided)
        {
            first.EdgeSubdivisions.Add(new(63, 0, MapCellEdge.East, 8));
            second.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.West, 8));
        }
        int segments = subdivided ? 8 : 1;
        var pairs = Enumerable.Range(0, segments + 1).Select(z =>
            (First: new MapLatticeVertex("ground", MapLatticeAddress.Create(64L * segments, z, segments)),
             Second: new MapLatticeVertex("adjoining", MapLatticeAddress.Create(64L * segments, z, segments)))).ToArray();
        first.Records.Add(new MapSurfaceSeam("seam", new(first.Key, pairs[0].First, pairs[^1].First),
            new(second.Key, pairs[0].Second, pairs[^1].Second), pairs));
        var view = MapScopedSurfaces.CompleteView(surfaces);
        Assert.Empty(MapSeamValidator.Validate(Assert.IsType<MapSurfaceSeam>(first.Records[0]), view));
        var compiled = MapSurfaceCompiler.Compile(surfaces.Refs[0], first);
        var partitions = compiled.Faces.Select((face, i) =>
        {
            var triangle = compiled.ExactTriangle(face);
            return new MapTerrainPartition(face.Key, null, compiled.Paint[i], [triangle.A, triangle.B, triangle.C]);
        }).ToArray();
        return (view, partitions);
    }
}
