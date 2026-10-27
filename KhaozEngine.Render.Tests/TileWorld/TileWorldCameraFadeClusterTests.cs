using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The camera fade reaching prop-layer clusters through the view: the faded placement's colour dissolves,
/// its shadow stays whole, its neighbours are untouched, a roof the roof rule hides stays hidden, and the target
/// outline does not float around an object the camera is inside.</summary>
public class TileWorldCameraFadeClusterTests
{
    const float Half = 0.075f;

    static TilePropLayerDefinition Trees() => new()
    {
        Id = "fade_trees",
        ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "fade_tree" },
        DrawRadius = 192f,
        LodDistance = 0f,
        CastsShadows = true,
    };

    sealed class Rig : IDisposable
    {
        public TileWorldDocument Doc { get; } = TileRenderTestData.RoadWorld();
        public TileWorldCatalogs Catalogs { get; } = TileCameraFadeTests.Catalogs();
        public RecordingTileWorldScene Scene { get; } = new();
        public TileWorldView View { get; }
        public TileObject Faded { get; }
        public TileObject Neighbour { get; }

        public Rig()
        {
            Faded = Doc.AddObject("fade_tree", 20, 20, 0, 0);
            Neighbour = Doc.AddObject("fade_tree", 28, 20, 0, 0);
            View = new TileWorldView(Scene, Doc, Catalogs, new TileCameraFadeTests.ShapeResolver(),
                new TileWorldViewOptions { PropLayers = new[] { Trees() } },
                new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 16, MaxHlodAppliesPerPump = 16 },
                new ImmediateDispatcher());
            View.LoadRegion(TileRenderTestData.Region, TileRegionResidencyState.Gameplay);
            View.CameraFade = new TileCameraFadeOptions { Eligible = _ => true };
        }

        public Vector3 Anchor(TileObject o) =>
            TileObjectPlacement.AnchorPosition(Doc, Catalogs.Archetype(o.ArchetypeId)!, o);

        public void FadeAndDraw(int halfSteps)
        {
            Vector3 canopy = Anchor(Faded) + new Vector3(0f, 3f, 0f);
            for (int i = 0; i < halfSteps; i++) View.UpdateCameraFade(canopy, Half);
            View.Draw(Anchor(Faded));
        }

        public void Dispose() => View.Dispose();
    }

    static bool At(PropPlacement placement, Vector3 anchor) =>
        MathF.Abs(placement.X - anchor.X) < 1e-3f && MathF.Abs(placement.Z - anchor.Z) < 1e-3f;

    [Fact]
    public void APartlyFadedLayerPlacementDissolvesColourAndKeepsItsShadow()
    {
        using var rig = new Rig();
        rig.FadeAndDraw(1);
        Vector3 faded = rig.Anchor(rig.Faded);
        Vector3 neighbour = rig.Anchor(rig.Neighbour);

        var colour = Assert.Single(rig.Scene.ClusterColourOnlyPropDraws);
        Assert.True(At(Assert.Single(colour.Placements), faded));
        Assert.Equal(0.5f, colour.DissolveFloor, 4);
        Assert.Contains(rig.Scene.ClusterShadowPropDraws,
            draw => draw.DissolveFloor == 0f && draw.Placements.Any(p => At(p, faded)));
        IEnumerable<PropPlacement> regular = rig.Scene.ClusterPropDraws.SelectMany(d => d.Placements);
        Assert.DoesNotContain(regular, p => At(p, faded));
        Assert.Contains(regular, p => At(p, neighbour));
    }

    [Fact]
    public void AFullyFadedLayerPlacementIsShadowOnly()
    {
        using var rig = new Rig();
        rig.FadeAndDraw(2);
        Vector3 faded = rig.Anchor(rig.Faded);

        Assert.Empty(rig.Scene.ClusterColourOnlyPropDraws);
        Assert.Contains(rig.Scene.ClusterShadowPropDraws, draw => draw.Placements.Any(p => At(p, faded)));
        Assert.DoesNotContain(rig.Scene.ClusterPropDraws.SelectMany(d => d.Placements), p => At(p, faded));
    }

    [Fact]
    public void AFadedPlacementIsNotOutlined()
    {
        using var plain = new Rig();
        plain.View.SetOutlinedObject(plain.Faded.Id, Color.White);
        plain.View.Draw(plain.Anchor(plain.Faded));
        Assert.NotEmpty(plain.Scene.OutlineGroups.SelectMany(g => g.Parts));

        using var rig = new Rig();
        rig.View.SetOutlinedObject(rig.Faded.Id, Color.White);
        rig.FadeAndDraw(1);
        Assert.Empty(rig.Scene.OutlineGroups.SelectMany(g => g.Parts));
    }

    [Fact]
    public void WithNoFadesSubmissionIsUnchanged()
    {
        using var off = new Rig();
        off.View.CameraFade = null;
        off.View.Draw(off.Anchor(off.Faded));
        using var idle = new Rig();
        idle.View.UpdateCameraFade(idle.Anchor(idle.Faded) + new Vector3(0f, 40f, 0f), Half);
        idle.View.Draw(idle.Anchor(idle.Faded));

        Assert.Equal(Describe(off.Scene), Describe(idle.Scene));
        Assert.Empty(idle.Scene.ClusterColourOnlyPropDraws);
    }

    [Fact]
    public void ARoofHiddenByTheRuleStaysHiddenUnderAFade()
    {
        using var rig = new TileWorldRoofPropLayerTests.Rig();
        rig.View.RoofMode = RoofVisibility.AlwaysHidden;
        rig.View.CameraFade = new TileCameraFadeOptions { Eligible = _ => true };
        TileObject roof = rig.Doc.AllObjects().Where(o => o.ArchetypeId == "roof_flat").OrderBy(o => o.Id).First();
        Vector3 eye = Enclosed(rig, roof);

        rig.View.UpdateCameraFade(eye, Half);
        Assert.Equal(0.5f, rig.View.CameraFadeOf(roof.Id), 4);
        rig.View.Draw(TileWorldRoofPropLayerTests.Focus(0f));

        Assert.Empty(rig.Scene.ClusterColourOnlyPropDraws);
        Assert.DoesNotContain(rig.Scene.ClusterPropDraws.SelectMany(d => d.Placements), p => p.Id == "roof_flat");
    }

    // A point the roof's own greybox triangles enclose.
    static Vector3 Enclosed(TileWorldRoofPropLayerTests.Rig rig, TileObject roof)
    {
        TileObjectArchetype archetype = TileRenderTestData.Catalogs.Archetype(roof.ArchetypeId)!;
        Vector3 anchor = TileObjectPlacement.AnchorPosition(rig.Doc, archetype, roof);
        (Vector3[] positions, int[] indices) =
            TileCameraFadeTests.Flatten(new TileWorldRoofPropLayerTests.Resolver().Resolve(archetype)!);
        Vector3 min = positions.Aggregate(Vector3.Min);
        Vector3 max = positions.Aggregate(Vector3.Max);
        for (int i = 1; i < 20; i++)
        {
            Vector3 local = Vector3.Lerp(min, max, i / 20f);
            if (MathF.Abs(MeshContainment.WindingNumber(positions, indices, local)) >= 0.5f) return anchor + local;
        }
        throw new InvalidOperationException("The greybox roof encloses no point on its box diagonal.");
    }

    static string Describe(RecordingTileWorldScene scene) => string.Join("|",
        scene.ClusterPropDraws.Select(d => $"p{d.Placements.Count}:{d.DissolveFloor}")
            .Concat(scene.ClusterShadowPropDraws.Select(d => $"s{d.Placements.Count}:{d.DissolveFloor}"))
            .Concat(scene.ClusterMergedDraws.Select(d => $"m{d.Dissolve}")));

    sealed class ImmediateDispatcher : IChunkBuildDispatcher
    {
        public void Schedule(Action build) => build();
        public void Drain() { }
    }
}
