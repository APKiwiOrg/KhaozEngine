using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldViewAnimatedFoliageTests
{
    static TileWorldDocument World()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.AddObject("bush", 2, 2, 0, 1);
        doc.AddObject("bush", 18, 2, 0, 0);
        doc.AddObject("tree", 8, 2, 0, 0);
        return doc;
    }

    static TileWorldViewOptions Options() => new()
    {
        PropDrawRadius = 50f,
        AnimatedFoliageArchetypes = new HashSet<string>(StringComparer.Ordinal) { "bush" },
        GroundCover = new GroundCoverRenderOptions { DrawRadius = 0f, QualityDensity = 0f },
    };

    static TileWorldView View(RecordingTileWorldScene scene, TileWorldDocument doc, TileWorldViewOptions options) =>
        new(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), options);

    [Fact]
    public void OptedInAuthoredPropsDrawOnceAtFullDensityWhenShortGrassIsOff()
    {
        TileWorldDocument doc = World();
        TileObject[] authored = doc.GetRegion(TileRenderTestData.Region)!.Objects.ToArray();
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc, Options());
        view.LoadRegion(TileRenderTestData.Region);

        view.Draw(Vector3.Zero);

        TileFoliageDrawRecord foliage = Assert.Single(scene.FoliageDraws);
        Assert.IsType<GroundCoverBatch>(foliage.Instances);
        Assert.Equal(2, foliage.Drawn);
        Assert.All(foliage.Instances, instance =>
        {
            Assert.Equal("bush", instance.ModelId);
            Assert.Equal(0f, instance.ThinningRank);
        });
        Assert.Equal(new Vector3(2.5f, 0f, -2.5f), foliage.Instances[0].Position);
        Matrix4x4 expected = Matrix4x4.CreateRotationY(-MathF.PI / 2f) *
                             Matrix4x4.CreateTranslation(2.5f, 0f, -2.5f);
        Assert.Equal(expected, foliage.Instances[0].Transform);
        Assert.True(foliage.Options.UseGpuBatches);
        Assert.Equal(GroundCoverFadeMode.HeightScale, foliage.Options.FadeMode);
        Assert.False(foliage.Options.CastsShadows);
        Assert.Equal(50f, foliage.Options.DrawRadius);
        Assert.Equal(1f, foliage.Options.QualityDensity);
        Assert.Equal(1f, foliage.Options.DistantDensity);
        Assert.Equal("tree", Assert.Single(Assert.Single(scene.PropDraws).Placements).Id);
        Assert.Equal(3, view.LastDrawnProps);
        Assert.Equal(0, view.GeneratedCoverCount);
        Assert.Equal(0, view.LastDrawnCover);
        Assert.All(authored, obj => Assert.Same(obj, doc.FindObject(obj.Id)));
    }

    [Fact]
    public void DefaultOptionsKeepEveryAuthoredPropOnTheExistingPath()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, World(), new TileWorldViewOptions());
        view.LoadRegion(TileRenderTestData.Region);

        view.Draw(Vector3.Zero);

        Assert.Empty(scene.FoliageDraws);
        Assert.Equal(3, Assert.Single(scene.PropDraws).Placements.Count);
        Assert.Equal(3, view.LastDrawnProps);
    }

    [Fact]
    public void RepeatedDrawsReuseTheSplitAndReadLiveWindAndInteractions()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldViewOptions options = Options();
        using TileWorldView view = View(scene, World(), options);
        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(Vector3.Zero);
        IReadOnlyList<GroundCoverInstance> before = Assert.Single(scene.FoliageDraws).Instances;
        options.GroundCover.WindDirection = new Vector2(.3f, -.8f);
        options.GroundCover.WindStrength = .6f;
        options.GroundCover.WindSpeed = 3f;
        options.GroundCover.WindSpatialFrequency = .7f;
        options.GroundCover.Interactors = [new FoliageInteractor(new Vector3(2f, 0f, -2f), 1.5f)];
        options.PropDrawRadius = 48f;
        scene.ClearFrame();

        view.Draw(Vector3.Zero);

        TileFoliageDrawRecord after = Assert.Single(scene.FoliageDraws);
        Assert.Same(before, after.Instances);
        Assert.Equal(new Vector2(.3f, -.8f), after.Options.WindDirection);
        Assert.Equal(.6f, after.Options.WindStrength);
        Assert.Equal(3f, after.Options.WindSpeed);
        Assert.Equal(.7f, after.Options.WindSpatialFrequency);
        Assert.Equal(new FoliageInteractor(new Vector3(2f, 0f, -2f), 1.5f), Assert.Single(after.Options.Interactors));
        Assert.Equal(48f, after.Options.DrawRadius);
        Assert.Empty(scene.FoliageReleases);
    }

    [Fact]
    public void DirtyRebuildReleasesTheSplitBeforeTheNewPropsAreDrawn()
    {
        TileWorldDocument doc = World();
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc, Options());
        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(Vector3.Zero);
        IReadOnlyList<GroundCoverInstance> before = Assert.Single(scene.FoliageDraws).Instances;
        doc.AddObject("bush", 24, 2, 0, 0);
        view.MarkDirty(TileRenderTestData.Region, 0);

        view.Flush(int.MaxValue);

        Assert.Contains(scene.FoliageReleases, batch => ReferenceEquals(before, batch));
        scene.ClearFrame();
        view.Draw(Vector3.Zero);
        TileFoliageDrawRecord after = Assert.Single(scene.FoliageDraws);
        Assert.NotSame(before, after.Instances);
        Assert.Equal(3, after.Drawn);
        Assert.Equal(4, view.LastDrawnProps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnloadAndDisposeReleaseTheAnimatedBatchOnce(bool unload)
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, World(), Options());
        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(Vector3.Zero);
        IReadOnlyList<GroundCoverInstance> before = Assert.Single(scene.FoliageDraws).Instances;

        if (unload) view.UnloadRegion(TileRenderTestData.Region);
        view.Dispose();
        view.Dispose();

        Assert.Single(scene.FoliageReleases, batch => ReferenceEquals(before, batch));
    }

    [Fact]
    public void InPlaceSelectionChangesRebuildTheSplitAndRestoreOrdinaryPropsWhenEmpty()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldViewOptions options = Options();
        var selection = (HashSet<string>)options.AnimatedFoliageArchetypes;
        using TileWorldView view = View(scene, World(), options);
        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(Vector3.Zero);
        IReadOnlyList<GroundCoverInstance> before = Assert.Single(scene.FoliageDraws).Instances;
        selection.Remove("bush");
        selection.Add("tree");
        scene.ClearFrame();

        view.Draw(Vector3.Zero);

        TileFoliageDrawRecord after = Assert.Single(scene.FoliageDraws);
        Assert.Equal("tree", Assert.Single(after.Instances).ModelId);
        Assert.Equal(2, Assert.Single(scene.PropDraws).Placements.Count);
        Assert.Contains(scene.FoliageReleases, batch => ReferenceEquals(before, batch));
        selection.Clear();
        scene.ClearFrame();
        view.Draw(Vector3.Zero);
        Assert.Empty(scene.FoliageDraws);
        Assert.Equal(3, Assert.Single(scene.PropDraws).Placements.Count);
        Assert.Contains(scene.FoliageReleases, batch => ReferenceEquals(after.Instances, batch));
    }

    [Fact]
    public void ArchetypeOverridesInvalidateTheCachedGroundListWithoutChangingAuthoredObjects()
    {
        TileWorldDocument doc = World();
        TileObject bush = doc.GetRegion(TileRenderTestData.Region)!.Objects.First(obj => obj.ArchetypeId == "bush");
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc, Options());
        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(Vector3.Zero);
        IReadOnlyList<GroundCoverInstance> before = Assert.Single(scene.FoliageDraws).Instances;
        view.OverrideArchetype(bush.Id, "tree");
        scene.ClearFrame();

        view.Draw(Vector3.Zero);

        Assert.Single(Assert.Single(scene.FoliageDraws).Instances);
        Assert.Equal(2, Assert.Single(scene.PropDraws).Placements.Count);
        Assert.Contains(scene.FoliageReleases, batch => ReferenceEquals(before, batch));
        Assert.Equal("bush", doc.FindObject(bush.Id)!.ArchetypeId);
    }

    [Fact]
    public void RemovingTheLastDrawnPropReleasesTheCachedSplit()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        TileObject bush = doc.AddObject("bush", 2, 2, 0, 0);
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc, Options());
        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(Vector3.Zero);
        IReadOnlyList<GroundCoverInstance> before = Assert.Single(scene.FoliageDraws).Instances;
        view.OverrideArchetype(bush.Id, "missing-archetype");
        scene.ClearFrame();

        view.Draw(Vector3.Zero);

        Assert.Empty(scene.FoliageDraws);
        Assert.Empty(scene.PropDraws);
        Assert.Equal(0, view.LastDrawnProps);
        Assert.Contains(scene.FoliageReleases, batch => ReferenceEquals(before, batch));
    }
}
