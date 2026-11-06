using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class PaintAdmissionRefusalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Paint_target_distinguishes_missing_from_nonphysical(bool missing)
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        var set = new MapSurfaceSet();
        set.Refs.Add(missing ? surface : surface with { Role = MapSurfaceRole.PaintOverride });
        set.Patches.Add(patch.Key, patch);
        string target = missing ? "missing" : surface.Id;
        var error = Assert.Throws<MapDocumentException>(() => MapPartialPaintCompiler.Compile(MapScopedSurfaces.CompleteView(set), target));
        Assert.Equal(missing ? MapDocumentRefusal.ReferenceMissing : MapDocumentRefusal.SurfacePaintTargetInvalid, error.Reason);
        Assert.Equal(target, error.SurfaceId);
        set.Refs[0] = surface;
        Assert.Empty(MapPartialPaintCompiler.Compile(MapScopedSurfaces.CompleteView(set), surface.Id).Regions);
    }

    [Fact]
    public void Paint_requires_the_captured_paint_role()
    {
        var doc = new MapDocument();
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        doc.Surfaces.Refs.Add(surface);
        doc.Surfaces.Patches.Add(patch.Key, patch);
        var source = MapDocumentSurfaceSource.Capture(doc);
        var scope = ScopeFixtures.Whole(doc);
        var view = ScopeFixtures.Acquire(source, scope with { Roles = [MapSurfaceRole.SupportFloor] });
        var error = Assert.Throws<MapDocumentException>(() => MapPartialPaintCompiler.Compile(view, surface.Id));
        Assert.Equal(MapDocumentRefusal.AcquisitionIncomplete, error.Reason);
        Assert.Equal(MapAcquireStatus.Complete, error.AcquireStatus);
        Assert.Equal("Scope.Roles", error.FieldPath);
        Assert.Empty(MapPartialPaintCompiler.Compile(ScopeFixtures.Acquire(source, scope), surface.Id).Regions);
    }

    [Fact]
    public void Misplaced_priority_carries_the_surface_and_authored_value()
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(surface with { PaintPriority = 3 }, patch));
        Assert.Equal(MapDocumentRefusal.SurfacePaintPriorityMisplaced, error.Reason);
        Assert.Equal(surface.Id, error.SurfaceId);
        Assert.Equal("PaintPriority", error.FieldPath);
        Assert.Equal(3L, error.ActualInteger);
        Assert.NotEmpty(MapSurfaceCompiler.Compile(surface, patch).Faces);
    }

    [Fact]
    public void Paint_refuses_an_unfinished_acquisition_before_reading_geometry()
    {
        var doc = new MapDocument();
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        doc.Surfaces.Refs.Add(surface);
        doc.Surfaces.Patches.Add(patch.Key, patch);
        var source = MapDocumentSurfaceSource.Capture(doc);
        var scope = ScopeFixtures.Whole(doc);
        var view = ScopeFixtures.Acquire(source, scope with { Limits = scope.Limits with { MaxCandidatePatches = 0 } });
        Assert.Equal(MapAcquireStatus.CapacityExceeded, view.Status);
        var error = Assert.Throws<MapDocumentException>(() => MapPartialPaintCompiler.Compile(view, surface.Id));
        Assert.Equal(MapDocumentRefusal.AcquisitionIncomplete, error.Reason);
        Assert.Equal(MapAcquireStatus.CapacityExceeded, error.AcquireStatus);
        Assert.Equal(surface.Id, error.SurfaceId);
        Assert.Empty(MapPartialPaintCompiler.Compile(ScopeFixtures.Acquire(source,
            scope with { Limits = scope.Limits with { MaxCandidatePatches = 1 } }), surface.Id).Regions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Material_boundary_faults_keep_the_paint_surface(bool duplicate)
    {
        void Run(bool broken)
        {
            if (duplicate) MapPartialPaintCompiler.RequireUniqueMaterialEdge(broken, "paint");
            else MapPartialPaintCompiler.RequireClosedMaterialBoundary(broken ? 3 : 2, "paint");
        }
        var error = Assert.Throws<MapDocumentException>(() => Run(true));
        Assert.Equal(MapDocumentRefusal.SurfacePaintBoundaryInvalid, error.Reason);
        Assert.Equal("paint", error.SurfaceId);
        Run(false);
    }

    [Fact]
    public void Paint_budget_guard_accepts_the_exact_cap_and_keeps_unknown_counts_null()
    {
        var error = Assert.Throws<MapDocumentException>(() => MapPartialPaintCompiler.RequireWorkBudget(11, 10, "pair checks", "MaxPairChecks"));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("MaxPairChecks", error.Budget);
        Assert.Equal(11L, error.ActualCount);
        Assert.Equal(10L, error.Limit);
        MapPartialPaintCompiler.RequireWorkBudget(10, 10, "pair checks", "MaxPairChecks");
        error = Assert.Throws<MapDocumentException>(() => MapPartialPaintCompiler.RequireWorkBudget(null, 10, "pair checks", "MaxPairChecks"));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Null(error.ActualCount);
        Assert.Equal(10L, error.Limit);
    }

    [Fact]
    public void Completed_seam_producers_expose_their_ordered_typed_projection()
    {
        var (view, seam) = BoundaryFixtures.CoarseFineSeam(2, true, 1);
        var findings = MapSeamValidator.ValidateFindings(seam, view);
        Assert.NotEmpty(findings);
        Assert.Equal(MapSeamValidator.Validate(seam, view), findings.Select(f => f.Message));
        Assert.All(findings, f => Assert.Equal(seam.Id, f.RecordId));
        var (validView, validSeam) = BoundaryFixtures.CoarseFineSeam(2, true, 0);
        Assert.Empty(MapSeamValidator.ValidateFindings(validSeam, validView));
        var (cornerView, _, pos) = BoundaryFixtures.NegativeSlotPair();
        var patch = pos.Patch;
        var set = new MapSurfaceSet();
        set.Refs.AddRange(cornerView.Surfaces);
        set.Patches.Add(patch.Key, patch);
        var cornerFindings = MapSeamValidator.ValidateCornerDependencyFindings(patch, MapScopedSurfaces.CompleteView(set));
        Assert.Equal(2, cornerFindings.Count);
        Assert.Equal(new int?[] { 0, 1 }, cornerFindings.Select(f => f.Index));
        Assert.All(cornerFindings, finding =>
        {
            Assert.Equal(MapDocumentRefusal.PatchUnavailable, finding.Reason);
            Assert.Equal(patch.Key, finding.Patch);
            Assert.Equal(patch.CornerDependencies[0].Owner.Patch, finding.ExpectedPatch);
        });
        Assert.Empty(MapSeamValidator.ValidateCornerDependencyFindings(patch, cornerView));
    }
}
