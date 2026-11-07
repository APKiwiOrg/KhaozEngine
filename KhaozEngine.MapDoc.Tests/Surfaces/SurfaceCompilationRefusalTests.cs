using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceCompilationRefusalTests
{
    [Fact]
    public void Physical_compilation_refuses_paint_without_changing_the_patch()
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        var paint = surface with { Role = MapSurfaceRole.PaintOverride, PaintTargetSurfaceId = "target" };
        var cells = patch.Cells.ToArray();
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(paint, patch));
        Assert.Equal(MapDocumentRefusal.SurfacePaintPhysicalCompileUnsupported, error.Reason);
        Assert.Equal(surface.Id, error.SurfaceId);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(cells, patch.Cells);
        Assert.NotEmpty(MapSurfaceCompiler.Compile(surface, patch).Faces);
    }

    [Fact]
    public void Effective_patch_refusal_keeps_the_requested_key_and_status()
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        var set = new MapSurfaceSet();
        set.Refs.Add(surface);
        var error = Assert.Throws<MapDocumentException>(() => MapEffectiveGeometry.Patch(MapScopedSurfaces.CompleteView(set), patch.Key));
        Assert.Equal(MapDocumentRefusal.PatchUnavailable, error.Reason);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(MapPatchStatus.Unloaded, error.PatchStatus);
        set.Patches.Add(patch.Key, patch);
        Assert.NotEmpty(MapEffectiveGeometry.Patch(MapScopedSurfaces.CompleteView(set), patch.Key).Faces);
    }

    [Fact]
    public void Shared_vertex_height_refusal_keeps_the_patch_and_address()
    {
        var key = new MapPatchKey("surface", 0, 0);
        var address = MapLatticeAddress.Corner(0, 0);
        var expected = new MapExactPoint(new(0, 1), new(1, 1), new(0, 1));
        var vertex = new MapSurfaceVertex(address, expected with { Y = new(2, 1) }, Vector3.Zero);
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceGeometryAdmission.RequireSharedVertexHeight(expected, vertex, key));
        Assert.Equal(MapDocumentRefusal.GeometryVertexHeightMismatch, error.Reason);
        Assert.Equal(key, error.Patch);
        Assert.Equal(key.SurfaceId, error.SurfaceId);
        Assert.Equal(address, error.Address);
        MapSurfaceGeometryAdmission.RequireSharedVertexHeight(expected, vertex with { Exact = expected }, key);
    }

    [Fact]
    public void Effective_normal_refuses_an_exactly_collapsed_triangle()
    {
        var a = new MapExactPoint(new(0, 1), new(0, 1), new(0, 1));
        var b = a with { X = new(1, 1) };
        var c = a with { X = new(2, 1) };
        var error = Assert.Throws<MapDocumentException>(() => MapEffectiveGeometry.Normal(new(a, b, c)));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal("Normal", error.Operation);
        Assert.Equal(-Vector3.UnitY, MapEffectiveGeometry.Normal(new(a, b, c with { Z = new(1, 1) })));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Frame_position_distinguishes_nonfinite_coordinates_from_the_radius(int branch)
    {
        Vector3 refused = branch switch
        {
            0 => new(float.PositiveInfinity, 0, 0),
            1 => new(0, 0, float.NegativeInfinity),
            _ => new(0, float.NaN, 0),
        };
        var patch = Compiled(default, refused);
        var error = Assert.Throws<MapDocumentException>(() => MapFrameLocal.ToFrame(patch, WorldFrame.Origin));
        Assert.Equal(MapDocumentRefusal.ValueNonfinite, error.Reason);
        Assert.Equal(branch == 0 ? "$.position.x" : branch == 1 ? "$.position.z" : "$.position.y", error.FieldPath);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(branch == 0 ? refused.X : branch == 1 ? refused.Z : refused.Y, error.ActualNumber);
        Assert.Single(MapFrameLocal.ToFrame(Compiled(default, Vector3.Zero), WorldFrame.Origin).LocalPositions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frame_radius_accepts_the_exact_boundary_and_reports_checked_overflow(bool overflow)
    {
        var frame = overflow ? new WorldFrame(-1, 0) : WorldFrame.Origin;
        var patch = Compiled(overflow ? new(long.MaxValue, 0, 0) : default,
            overflow ? Vector3.Zero : new(WorldFrame.MaxLocalRadius + 1, 0, 0));
        var error = Assert.Throws<MapDocumentException>(() => MapFrameLocal.ToFrame(patch, frame));
        Assert.Equal(MapDocumentRefusal.FrameRadiusExceeded, error.Reason);
        Assert.Equal((long)WorldFrame.MaxLocalRadius, error.Limit);
        Assert.Equal(patch.Key, error.Patch);
        if (overflow) Assert.Null(error.PositionX);
        else
        {
            Assert.Equal(WorldFrame.MaxLocalRadius + 1d, error.PositionX);
            Assert.Equal(0d, error.PositionZ);
        }
        var control = Compiled(new((long)frame.X * (long)WorldFrame.Grid, 0, 0), new(WorldFrame.MaxLocalRadius, 0, 0));
        Assert.Equal(new Vector3(WorldFrame.MaxLocalRadius, 0, 0), Assert.Single(MapFrameLocal.ToFrame(control, frame).LocalPositions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_legacy_sampling_distinguishes_recipe_and_selected_cell(bool outsideCell)
    {
        var doc = LegacyExteriorFixtures.Row("tagged");
        var patch = doc.Surfaces.Patches.Values.Single();
        var surface = doc.Surfaces.Refs.Single();
        for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Flags = MapCellFlags.None };
        var context = new MapEffectiveContext(doc.Surfaces, new Dictionary<string, MapEffectiveTransform>
        { ["Surface:" + surface.Id] = new(0, 0, 0, 0, 1) }, new string('a', 64));
        var view = MapScopedSurfaces.CompleteEffective(context);
        var invalidSurface = surface with { PresencePolicy = MapPresencePolicy.Native };
        var validated = new MapValidatedSurfacePatch(outsideCell ? surface : invalidSurface, patch);
        var point = new MapExactXz(new(1, 2), new(-1, 2));
        var error = Assert.Throws<MapDocumentException>(() => MapEffectiveGeometry.LegacyHeight(view, validated, outsideCell ? 64 : 0, point));
        Assert.Equal(outsideCell ? MapDocumentRefusal.SurfaceLegacySampleOutOfCell : MapDocumentRefusal.SurfaceLegacyRecipeRequired, error.Reason);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(surface.Id, error.SurfaceId);
        if (outsideCell) Assert.Equal(64, error.SlotCell);
        Assert.Equal(MapExactValue.FromSingle(MapLegacyBilinear.HeightMetres(surface, patch, point)),
            MapEffectiveGeometry.LegacyHeight(view, new(surface, patch), 0, point));
    }

    static MapCompiledPatch Compiled(MapSubmissionAnchor anchor, Vector3 offset) =>
        new(new("frame", 0, 0), MapSurfaceRole.SupportFloor, anchor, [], [], [offset], [], [], []);
}
