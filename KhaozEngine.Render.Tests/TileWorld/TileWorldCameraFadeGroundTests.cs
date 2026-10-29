using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The camera fade on the paths outside prop layers: ungrouped ground props and roofs, the silhouette and
/// the ungrouped target outline, and animated foliage, which the fade leaves alone.</summary>
public class TileWorldCameraFadeGroundTests
{
    const float Half = 0.075f;

    sealed class Rig : IDisposable
    {
        public TileWorldDocument Doc { get; } = TileRenderTestData.RoadWorld();
        public TileWorldCatalogs Catalogs { get; } = TileCameraFadeTests.Catalogs();
        public RecordingTileWorldScene Scene { get; } = new();
        public TileWorldView View { get; }
        public TileObject Faded { get; }
        public TileObject Neighbour { get; }

        public Rig(TileWorldViewOptions? options = null)
        {
            Faded = Doc.AddObject("fade_box", 20, 20, 0, 0);
            Neighbour = Doc.AddObject("fade_box", 26, 20, 0, 0);
            View = new TileWorldView(Scene, Doc, Catalogs, new TileCameraFadeTests.ShapeResolver(), options);
            View.LoadRegion(TileRenderTestData.Region);
            View.CameraFade = new TileCameraFadeOptions { Eligible = _ => true };
        }

        public Vector3 Anchor(TileObject o) =>
            TileObjectPlacement.AnchorPosition(Doc, Catalogs.Archetype(o.ArchetypeId)!, o);

        public void FadeAndDraw(int halfSteps)
        {
            Vector3 inside = Anchor(Faded) + new Vector3(0f, 2f, 0f);
            for (int i = 0; i < halfSteps; i++) View.UpdateCameraFade(inside, Half);
            View.Draw(Anchor(Faded));
        }

        public void Dispose() => View.Dispose();
    }

    static bool At(PropPlacement placement, Vector3 anchor) =>
        MathF.Abs(placement.X - anchor.X) < 1e-3f && MathF.Abs(placement.Z - anchor.Z) < 1e-3f;

    [Fact]
    public void AFadedGroundPropDissolvesColourAndKeepsItsShadow()
    {
        using var rig = new Rig();
        rig.FadeAndDraw(1);
        Vector3 faded = rig.Anchor(rig.Faded);

        var colour = Assert.Single(rig.Scene.ColourOnlyPropDraws);
        Assert.True(At(Assert.Single(colour.Record.Placements), faded));
        Assert.Equal(0.5f, colour.Dissolve, 4);
        Assert.Contains(rig.Scene.ShadowOnlyPropDraws, d => d.Placements.Any(p => At(p, faded)));
        IEnumerable<PropPlacement> regular = rig.Scene.PropDraws.SelectMany(d => d.Placements);
        Assert.DoesNotContain(regular, p => At(p, faded));
        Assert.Contains(regular, p => At(p, rig.Anchor(rig.Neighbour)));
    }

    [Fact]
    public void AFullyFadedGroundPropIsShadowOnly()
    {
        using var rig = new Rig();
        rig.FadeAndDraw(2);
        Vector3 faded = rig.Anchor(rig.Faded);

        Assert.Empty(rig.Scene.ColourOnlyPropDraws);
        Assert.Contains(rig.Scene.ShadowOnlyPropDraws, d => d.Placements.Any(p => At(p, faded)));
        Assert.DoesNotContain(rig.Scene.PropDraws.SelectMany(d => d.Placements), p => At(p, faded));
        Assert.Equal(1, rig.View.LastShadowOnlyProps);
    }

    [Fact]
    public void AnimatedFoliageNeverFades()
    {
        var options = new TileWorldViewOptions
        {
            AnimatedFoliageArchetypes = new HashSet<string>(StringComparer.Ordinal) { "fade_box" },
            AnimatedFoliageStyles = new Dictionary<string, AnimatedFoliageStyle>(StringComparer.Ordinal)
            {
                ["fade_box"] = new(new FoliageMotionProfile(.3f, .15f, .4f, .035f), true),
            },
        };
        using var rig = new Rig(options);
        rig.FadeAndDraw(2);

        Assert.Equal(0f, rig.View.CameraFadeOf(rig.Faded.Id));
        Assert.Empty(rig.Scene.ColourOnlyPropDraws);
    }

    [Fact]
    public void AFadedUngroupedRoofKeepsItsShadow()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        TileObject roof = doc.AllObjects().Where(o => catalogs.Archetype(o.ArchetypeId)!.IsRoof).OrderBy(o => o.Id).First();
        var scene = new RecordingTileWorldScene();
        var resolver = new GreyboxMeshResolver();
        using var view = new TileWorldView(scene, doc, catalogs, resolver);
        view.LoadRegion(TileRenderTestData.Region);
        view.RoofMode = RoofVisibility.AlwaysVisible;
        view.CameraFade = new TileCameraFadeOptions { Eligible = _ => true };
        TileObjectArchetype archetype = catalogs.Archetype(roof.ArchetypeId)!;
        Vector3 anchor = TileObjectPlacement.AnchorPosition(doc, archetype, roof);
        Vector3 eye = anchor + Enclosed(resolver.Resolve(archetype)!);

        view.UpdateCameraFade(eye, Half);
        view.Draw(anchor);

        // The house's roof pieces overlap, so the eye can sit in several. Each one it is in fades on its own.
        Assert.Contains(scene.ColourOnlyPropDraws, d => d.Record.Placements.Any(p => At(p, anchor)));
        Assert.All(scene.ColourOnlyPropDraws, d => Assert.Equal(0.5f, d.Dissolve, 4));
        Assert.Contains(scene.ShadowOnlyPropDraws, d => d.Placements.Any(p => At(p, anchor)));
        Assert.DoesNotContain(scene.PropDraws.SelectMany(d => d.Placements), p => At(p, anchor) && p.Id == roof.ArchetypeId);
    }

    [Fact]
    public void AFadedObjectIsNotSilhouettedOrOutlined()
    {
        using var plain = new Rig();
        plain.View.SetSilhouettedObject(plain.Faded.Id, Color.White);
        plain.View.SetOutlinedObject(plain.Faded.Id, Color.White);
        plain.View.Draw(plain.Anchor(plain.Faded));
        Assert.NotEmpty(plain.Scene.Silhouettes);
        Assert.NotEmpty(plain.Scene.OutlineGroups.SelectMany(g => g.Parts));

        using var rig = new Rig();
        rig.View.SetSilhouettedObject(rig.Faded.Id, Color.White);
        rig.View.SetOutlinedObject(rig.Faded.Id, Color.White);
        rig.FadeAndDraw(1);
        Assert.Empty(rig.Scene.Silhouettes);
        Assert.Empty(rig.Scene.OutlineGroups.SelectMany(g => g.Parts));
    }

    [Fact]
    public void WithNoFadesGroundSubmissionIsUnchanged()
    {
        using var off = new Rig();
        off.View.CameraFade = null;
        off.View.Draw(off.Anchor(off.Faded));
        using var idle = new Rig();
        idle.View.UpdateCameraFade(idle.Anchor(idle.Faded) + new Vector3(0f, 40f, 0f), Half);
        idle.View.Draw(idle.Anchor(idle.Faded));

        Assert.Equal(Describe(off.Scene), Describe(idle.Scene));
        Assert.Empty(idle.Scene.ColourOnlyPropDraws);
    }

    static string Describe(RecordingTileWorldScene scene) => string.Join("|",
        scene.PropDraws.Select(d => $"p{d.Placements.Count}:{d.Drawn}")
            .Concat(scene.ShadowOnlyPropDraws.Select(d => $"s{d.Placements.Count}:{d.Drawn}")));

    // A point inside the mesh, searched along its box diagonal, as an anchor-relative offset.
    static Vector3 Enclosed(IReadOnlyList<GltfMeshPart> parts)
    {
        (Vector3[] positions, int[] indices) = TileCameraFadeTests.Flatten(parts);
        Vector3 min = positions.Aggregate(Vector3.Min);
        Vector3 max = positions.Aggregate(Vector3.Max);
        for (int i = 1; i < 20; i++)
        {
            Vector3 local = Vector3.Lerp(min, max, i / 20f);
            if (MathF.Abs(MeshContainment.WindingNumber(positions, indices, local)) >= 0.5f) return local;
        }
        throw new InvalidOperationException("The mesh encloses no point on its box diagonal.");
    }
}
