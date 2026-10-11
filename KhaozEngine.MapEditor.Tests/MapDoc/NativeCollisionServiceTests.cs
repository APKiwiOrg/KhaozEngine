using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public class NativeCollisionServiceTests
{
    [Fact]
    public void DryRun_NeverWritesOrResizesTheMesh()
    {
        using var f = new NativeCollisionToolFixture();
        byte[] before = f.ReadAssetDirectoryDigest();
        var edit = f.Service.SetHeights("wall-variant", 0.1f, 2.4f, dryRun: true);
        Assert.False(edit.Applied);
        Assert.NotEqual(edit.BeforeSha256, edit.AfterSha256);
        Assert.Equal(before, f.ReadAssetDirectoryDigest());
        Assert.Contains("wall-1", edit.AffectedPlacementIds);
        Assert.True(edit.Effects.Invalidates.HasFlag(MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency));
        // The placements are listed once, in AffectedPlacementIds. A collider edit names no dependency.
        Assert.Empty(edit.Effects.DependencyIds);
    }

    [Fact]
    public void PlacementWhoseExtentIsUnknown_ReportsUnbounded()
    {
        using var f = new NativeCollisionToolFixture();
        // Resolver 2 with wall-1 bound to a support floor that has no patch payload, so its support cannot resolve.
        f.Session.WithDocument((document, _) =>
        {
            document.ResolverIdentity = new(1, 2);
            document.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
            var surface = new MapSurfaceRef("ground", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor,
                MapPresencePolicy.Native, null, null, "");
            document.Surfaces.Refs.Add(surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, Array.Empty<KeyValuePair<MapPatchKey, string>>()),
            });
            MapPlacement wall = document.Placements.Single(p => p.Id == "wall-1");
            wall.Y = null;
            wall.SupportBinding = new MapSupportBinding(MapSupportBindingKind.Surface, "ground", null, 0.25f, 1f, 2f);
            return 0;
        });
        var edit = f.Service.SetHeights("wall-variant", 0.1f, 2.4f, dryRun: true);
        Assert.Equal((NativeCollisionService.HeightEditInvalidation | MapNativeInvalidation.Unbounded, (MapBox3?)null, (MapBox3?)null),
            (edit.Effects.Invalidates, edit.Effects.OldBounds, edit.Effects.NewBounds));
    }

    [Fact]
    public void Apply_WritesNewContentAndReloadsWithTheNewCollider()
    {
        using var f = new NativeCollisionToolFixture();
        var edit = f.Service.SetHeights("wall-variant", 0.1f, 2.4f, dryRun: false);
        Assert.True(edit.Applied);
        using var reopened = f.Reopen();
        var m = reopened.Service.Measure("wall-1");
        Assert.Equal(edit.AfterSha256, m.ColliderSha256);
        Assert.Equal(2.3f, m.EffectiveTop - m.EffectiveBottom, 4);
    }

    [Fact]
    public void BakedMeshCollider_RefusesHeightEdits()
    {
        using var f = new NativeCollisionToolFixture();
        Assert.Contains("compound boxes", Assert.Throws<MapDocumentException>(() => f.Service.SetHeights("rock-mesh", 0f, 1f)).Message);
    }
}
