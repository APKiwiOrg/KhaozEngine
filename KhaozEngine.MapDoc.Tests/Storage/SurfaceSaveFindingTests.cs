using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceSaveFindingTests
{
    [Fact]
    public void WholeSaveAdapter_ForwardsTheFirstTopologyFindingAndTheOrderedSnapshot()
    {
        var set = new MapSurfaceSet();
        var a = MixedResolutionFixtures.Surface("a", 1, MapSurfaceRole.SupportFloor);
        var b = a with { Id = "b" };
        set.Refs.AddRange([a, b, a, b]);
        var findings = MapTopologyReferenceValidator.ValidateFindings(set.Refs, set.Patches.Values.ToArray());
        Assert.Equal(new[] { "a", "b" }, findings.Select(f => f.SurfaceId));
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceSaveGuard.Whole(set));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal("a", error.SurfaceId);
        Assert.Equal(findings, error.Findings);
        set.Refs.RemoveRange(2, 2);
        MapSurfaceSaveGuard.Whole(set);
    }

    [Fact]
    public void QueryGeometry_RefusesTheFirstFloatBeyondTheExactIntegerDomain()
    {
        var source = MapDocumentSurfaceSource.Capture(new MapDocument());
        // 2^63 exceeds signed long. Its preceding float, 2^63 - 2^39, is exact and representable.
        float beyond = MathF.ScaleB(1, 63);
        var scope = new MapSurfaceScope(WorldFrame.Origin, new(beyond, 0), new(beyond, 0), null, null,
            [MapSurfaceRole.SupportFloor], null, new MapQueryLimits());
        var error = Assert.Throws<MapDocumentException>(() => source.FindPatches(scope));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal("FindPatches", error.Operation);
        Assert.Equal("scope", error.FieldPath);
        float boundary = MathF.BitDecrement(beyond);
        var accepted = source.FindPatches(scope with { LocalMin = new(boundary, 0), LocalMax = new(boundary, 0) });
        Assert.Equal(MapFindStatus.Complete, accepted.Status);
        Assert.Empty(accepted.Patches);
    }
}
