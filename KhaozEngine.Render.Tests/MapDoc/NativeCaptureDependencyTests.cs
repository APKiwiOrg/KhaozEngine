using System;
using System.Numerics;
using System.Linq;
using System.IO;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeCaptureDependencyTests
{
    [Fact]
    public void DependencyDiscovery_UsesTheDeclaredSelectedResourceAllowance()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var closure = NativeRenderAssetFixtures.Create("foliage");
        fixture.Document.NativeAssets = closure.Roots.ToList();
        for (int i = 1; i <= 2; i++)
            fixture.Document.Placements.Add(new()
            {
                Id = "selected-" + i,
                Kind = "cosmetic",
                AssetId = "A",
                NumericId = i,
                X = i - 1,
                Y = 0,
                Z = 0
            });
        fixture.Document.NumericIdHighWaterMark = 2;
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var source = MapSceneSource.Capture(document, closure, new(MaxSelectedResources: 1)).Source!;
        var proof = MapRenderExtentValidator.Validate(closure, source.RequiredAssetIds, new(new())).Proofs!;
        source = source.WithRenderExtents(proof).Source!;
        Assert.DoesNotContain(source.Data.Metadata, m => m.Key.Kind == "Light");
        var request = CaptureDependencyFixtures.Request() with { Closure = closure, Source = source };
        var result = MapCaptureDependencies.Compute(request);
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Equal(new MapSceneCapacity("MaxSelectedResources", 2, 1), result.Capacity);
    }

    [Fact]
    public void LiteralClosure_DiscoversBothOffCameraCastersAndExcludesTheFarPlacement()
    {
        var request = CaptureDependencyFixtures.Request();
        var lightBounds = request.Source.Data.Metadata.Single(m => m.Key.Id == "point-a").Bounds;
        var light = request.Source.Acquire(MapCaptureDependencies.Scope(request.Scope, lightBounds));
        Assert.True(light.Scene is not null, string.Join(" | ", light.Findings));
        var result = MapCaptureDependencies.Compute(request);
        Assert.True(result.Status == MapSceneStatus.CompleteForScope, result.Status + " " + string.Join(" | ", result.RequiredScopes.Select(s => string.Join("/", request.Source.Acquire(s).Findings))));
        var acquisitions = result.RequiredScopes.Select(request.Source.Acquire).ToArray();
        Assert.All(acquisitions, a => Assert.NotNull(a.Scene));
        var keys = acquisitions.SelectMany(a => a.Witness.Required).ToArray();
        Assert.Contains(keys, k => k.Id == "sun-caster" && k.Kind == "Placement");
        Assert.Contains(keys, k => k.Id == "point-caster" && k.Kind == "Placement");
        Assert.Contains(keys, k => k.Id == "point-a" && k.Kind == "Light");
        Assert.DoesNotContain(keys, k => k.Id == "unrelated-far");
        Assert.NotEmpty(result.LightingDigest);
        var changed = MapCaptureDependencies.Compute(request with { Framing = request.Framing with { FarPlane = 31 } });
        Assert.NotEqual(result.LightingDigest, changed.LightingDigest);
    }
    [Theory]
    [InlineData("sun-caster")]
    [InlineData("point-caster")]
    public void MissingDependency_RefusesBeforeANewCaptureSourceCanBeCreated(string id)
    {
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-capture-closure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (document, closure) = CaptureDependencyFixtures.Create(directory);
            MapDocumentFile.SaveTiled(document, directory);
            using var disk = MapDocumentSource.OpenTiled(directory);
            var source = MapSceneSource.Capture(disk, closure).Source!;
            var request = CaptureDependencyFixtures.Request() with { Closure = closure, Source = source };
            var placement = document.Placements.Single(p => p.Id == id);
            var coord = new MapTileCoord((int)Math.Floor(placement.X / document.TileSize), (int)Math.Floor(placement.Z / document.TileSize));
            var tile = disk.Tiles.Entries.Single(e => e.Coord.Equals(coord));
            File.Delete(MapTileFile.PathOf(directory, coord, tile.Hash));
            // The earlier frozen source retains its admitted snapshot. A new capture generation must refuse.
            var replacement = MapSceneSource.Capture(disk, closure);
            Assert.Equal(MapSceneSourceStatus.Missing, replacement.Status); Assert.Null(replacement.Source);
            Assert.True(MapCapturePreparation.Prepare(request).Witness.Scene.CompleteForScope);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void FramedCamera_ReducesBeforeItsSingleLocalRounding()
    {
        var original = CaptureDependencyFixtures.Request().Framing with
        {
            Eye = new(KhaozEngine.Primitives.WorldFrame.Origin, new(.1234567f, 8.12345f, .7654321f)),
            Target = new(KhaozEngine.Primitives.WorldFrame.Origin, new(1.234567f, 1.11111f, .3333333f))
        };
        var frame = new KhaozEngine.Primitives.WorldFrame(250, -250);
        var translated = original with { Eye = original.Eye with { Frame = frame }, Target = original.Target with { Frame = frame } };
        var camera = translated.Camera; ((IRenderOriginAware)camera).RenderOrigin = frame.Anchor;
        Assert.Equal(original.Camera.ViewProjection, camera.ViewProjection);
        Assert.Equal(original.Camera.Forward, camera.Forward);
    }
    [Fact]
    public void FrozenLighting_CopiesEveryMutableShadowOption()
    {
        var original = CaptureDependencyFixtures.Lighting();
        var settings = original.Shadows;
        settings.ShadowNearDistance = 99; settings.PointShadows.MaxShadowedLights = 1;
        Assert.Equal(8, original.Shadows.ShadowNearDistance);
        Assert.Equal(8, original.Shadows.PointShadows.MaxShadowedLights);
        Assert.True(original.Shadows.BlobColor.R > 0);
    }
    [Fact]
    public void CaptureScope_IncludesOffCameraShadowAndLightDependencies()
    {
        var view = Matrix4x4.CreateLookAt(new(0, 8, 0), Vector3.Zero, Vector3.UnitZ);
        var projection = Matrix4x4.CreateOrthographic(8 * 640f / 480, 8, .1f, 32);
        var domain = ShadowCaptureDomain.Compute(view * projection, Vector3.Normalize(new(-1, 1, 0)),
            new ShadowSettings
            {
                Mode = ShadowMode.ShadowMap,
                ShadowMapResolution = 1024,
                ShadowCascadeCount = 2,
                ShadowNearDistance = 8,
                ShadowMaxDistance = 32
            }, 16);
        Assert.Equal(2, domain.CasterVolumes.Count);
        Assert.Contains(domain.CasterVolumes, b => b.Min.X <= -9 && b.Max.Y >= 5);
    }
}
