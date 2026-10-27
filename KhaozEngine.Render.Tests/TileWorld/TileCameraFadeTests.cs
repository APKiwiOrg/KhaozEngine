using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The view's camera fade: which placed objects contain the eye, by their drawn triangles rather than
/// their boxes, and how each one's fade moves over time.</summary>
public class TileCameraFadeTests
{
    const float Half = 0.075f;
    static readonly RegionCoord Region = TileRenderTestData.Region;

    internal static TileWorldCatalogs Catalogs() => TileWorldCatalogs.Merge(TileRenderTestData.Catalogs,
        TileWorldCatalogs.LoadJson("""
            { "archetypes": [
              { "id": "fade_box", "name": "fade box", "meshRef": "fade/box.glb" },
              { "id": "fade_tree", "name": "fade tree", "meshRef": "fade/tree.glb" },
              { "id": "fade_stump", "name": "fade stump", "meshRef": "fade/stump.glb" }
            ] }
            """, "camera fade tests"));

    static TileCameraFadeOptions Options(Func<TileObjectArchetype, bool>? eligible = null) =>
        new() { Eligible = eligible ?? (_ => true) };

    sealed class Rig : IDisposable
    {
        public TileWorldDocument Doc { get; } = TileRenderTestData.RoadWorld();
        public TileWorldCatalogs Catalogs { get; } = TileCameraFadeTests.Catalogs();
        public TileWorldView View { get; }

        public Rig(TileCameraFadeOptions? options = null, params (string Id, int X, int Z, int Plane)[] objects)
        {
            foreach ((string id, int x, int z, int plane) in objects) Doc.AddObject(id, x, z, plane, 0);
            View = new TileWorldView(new RecordingTileWorldScene(), Doc, Catalogs, new ShapeResolver());
            View.LoadRegion(Region);
            View.CameraFade = options ?? Options();
        }

        public TileObject Object(int index) => Doc.AllObjects().OrderBy(o => o.Id).ElementAt(index);

        // The object's mesh origin in world metres. Every test object has rotation 0, so local is world minus this.
        public Vector3 Anchor(TileObject o) =>
            TileObjectPlacement.AnchorPosition(Doc, Catalogs.Archetype(o.ArchetypeId)!, o);

        public void Step(Vector3 eye, float dt = Half, int times = 1)
        {
            for (int i = 0; i < times; i++) View.UpdateCameraFade(eye, dt);
        }

        public void Dispose() => View.Dispose();
    }

    [Fact]
    public void AnEyeInsideAnEligibleObjectFadesIn()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0));
        TileObject box = rig.Object(0);
        Vector3 eye = rig.Anchor(box) + new Vector3(0f, 2f, 0f);

        rig.Step(eye);
        Assert.Equal(0.5f, rig.View.CameraFadeOf(box.Id), 4);
        rig.Step(eye);
        Assert.Equal(1f, rig.View.CameraFadeOf(box.Id), 4);
        Assert.Equal(1, rig.View.CameraFadedCount);
    }

    [Fact]
    public void AnEyeUnderTheCanopyLeavesTheTreeDrawn()
    {
        using var rig = new Rig(null, ("fade_tree", 20, 20, 0));
        TileObject tree = rig.Object(0);
        Vector3 eye = rig.Anchor(tree) + new Vector3(1f, 1f, 0f);

        rig.Step(eye, 0.05f, 10);

        Assert.Equal(0f, rig.View.CameraFadeOf(tree.Id));
        Assert.Equal(0, rig.View.CameraFadedCount);
    }

    [Fact]
    public void IneligibleArchetypesNeverFade()
    {
        using var rig = new Rig(Options(a => a.Id != "fade_box"), ("fade_box", 20, 20, 0));
        TileObject box = rig.Object(0);

        rig.Step(rig.Anchor(box) + new Vector3(0f, 2f, 0f), Half, 4);

        Assert.Equal(0f, rig.View.CameraFadeOf(box.Id));
    }

    [Fact]
    public void LeavingFadesBackAndDropsTheEntry()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0));
        TileObject box = rig.Object(0);
        Vector3 anchor = rig.Anchor(box);
        rig.Step(anchor + new Vector3(0f, 2f, 0f), Half, 2);

        rig.Step(anchor + new Vector3(2f, 2f, 0f), Half, 2);

        Assert.Equal(0f, rig.View.CameraFadeOf(box.Id));
        Assert.Equal(0, rig.View.CameraFadedCount);
    }

    [Fact]
    public void HysteresisHoldsNearTheSurface()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0));
        TileObject box = rig.Object(0);
        Vector3 anchor = rig.Anchor(box);
        rig.Step(anchor + new Vector3(0f, 2f, 0f), Half, 2);

        rig.Step(anchor + new Vector3(1.3f, 2f, 0f));
        Assert.Equal(1f, rig.View.CameraFadeOf(box.Id), 4);

        rig.Step(anchor + new Vector3(1.5f, 2f, 0f));
        Assert.True(rig.View.CameraFadeOf(box.Id) < 1f);
    }

    [Fact]
    public void TheNearPlaneSlicingAFaceCounts()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0));
        TileObject box = rig.Object(0);

        rig.Step(rig.Anchor(box) + new Vector3(1.2f, 2f, 0f));

        Assert.True(rig.View.CameraFadeOf(box.Id) > 0f);
    }

    [Fact]
    public void FadeTimingIsFrameRateIndependent()
    {
        using var fine = new Rig(null, ("fade_box", 20, 20, 0));
        using var coarse = new Rig(null, ("fade_box", 20, 20, 0));
        TileObject a = fine.Object(0);
        TileObject b = coarse.Object(0);

        fine.Step(fine.Anchor(a) + new Vector3(0f, 2f, 0f), 0.01f, 15);
        coarse.Step(coarse.Anchor(b) + new Vector3(0f, 2f, 0f), 0.15f);

        Assert.Equal(1f, fine.View.CameraFadeOf(a.Id), 4);
        Assert.Equal(1f, coarse.View.CameraFadeOf(b.Id), 4);
    }

    [Fact]
    public void ANonPositiveOrNonFiniteStepHoldsFades()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0));
        TileObject box = rig.Object(0);
        Vector3 anchor = rig.Anchor(box);
        rig.Step(anchor + new Vector3(0f, 2f, 0f));

        foreach (float dt in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
        {
            rig.View.UpdateCameraFade(anchor + new Vector3(5f, 2f, 0f), dt);
            Assert.Equal(0.5f, rig.View.CameraFadeOf(box.Id), 4);
        }
    }

    [Fact]
    public void TwoOverlappingObjectsBothFade()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0), ("fade_box", 21, 20, 0));
        TileObject first = rig.Object(0);
        TileObject second = rig.Object(1);
        Vector3 between = (rig.Anchor(first) + rig.Anchor(second)) / 2f + new Vector3(0f, 2f, 0f);

        rig.Step(between, Half, 2);

        Assert.Equal(1f, rig.View.CameraFadeOf(first.Id), 4);
        Assert.Equal(1f, rig.View.CameraFadeOf(second.Id), 4);
        Assert.Equal(2, rig.View.CameraFadedCount);
    }

    [Fact]
    public void AnOverriddenArchetypeIsTestedAsItsOverride()
    {
        using var rig = new Rig(null, ("fade_tree", 20, 20, 0));
        TileObject tree = rig.Object(0);
        Vector3 canopy = rig.Anchor(tree) + new Vector3(0f, 3f, 0f);
        rig.View.OverrideArchetype(tree.Id, "fade_stump");

        rig.Step(canopy, Half, 4);

        Assert.Equal(0f, rig.View.CameraFadeOf(tree.Id));
    }

    [Fact]
    public void ObjectsOnEveryPlaneAreCandidates()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 1));
        TileObject box = rig.Object(0);

        rig.Step(rig.Anchor(box) + new Vector3(0f, 2f, 0f), Half, 2);

        Assert.Equal(1f, rig.View.CameraFadeOf(box.Id), 4);
    }

    [Fact]
    public void ObjectsOutsideGameplayResidencyNeverFade()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0));
        TileObject box = rig.Object(0);
        rig.View.LoadRegion(Region, TileRegionResidencyState.Decor);

        rig.Step(rig.Anchor(box) + new Vector3(0f, 2f, 0f), Half, 2);

        Assert.Equal(0f, rig.View.CameraFadeOf(box.Id));
    }

    [Fact]
    public void AnUnloadedObjectsFadeIsDropped()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0));
        TileObject box = rig.Object(0);
        Vector3 anchor = rig.Anchor(box);
        rig.Step(anchor + new Vector3(0f, 2f, 0f), Half, 2);

        rig.View.UnloadRegion(Region);
        rig.Step(anchor + new Vector3(0f, 2f, 0f));
        Assert.Equal(0, rig.View.CameraFadedCount);

        rig.View.LoadRegion(Region);
        rig.Step(anchor + new Vector3(5f, 2f, 0f));
        Assert.Equal(0f, rig.View.CameraFadeOf(box.Id));
    }

    [Fact]
    public void ClearingTheOptionsClearsFades()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0));
        TileObject box = rig.Object(0);
        rig.Step(rig.Anchor(box) + new Vector3(0f, 2f, 0f), Half, 2);

        rig.View.CameraFade = null;

        Assert.Equal(0, rig.View.CameraFadedCount);
        Assert.Equal(0f, rig.View.CameraFadeOf(box.Id));
    }

    [Fact]
    public void NarrowTestsRunOnlyForBoxHits()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0));
        TileObject box = rig.Object(0);

        rig.Step(rig.Anchor(box) + new Vector3(6f, 2f, 0f), Half, 10);
        Assert.Equal(0, rig.View.CameraContainmentTests);

        rig.Step(rig.Anchor(box) + new Vector3(0f, 2f, 0f));
        Assert.Equal(1, rig.View.CameraContainmentTests);
    }

    [Fact]
    public void TheFramePathDoesNotAllocateAfterWarmUp()
    {
        using var rig = new Rig(null, ("fade_box", 20, 20, 0), ("fade_tree", 24, 20, 0));
        Vector3 eye = rig.Anchor(rig.Object(0)) + new Vector3(0f, 2f, 0f);
        rig.Step(eye, Half, 3);

        long before = GC.GetAllocatedBytesForCurrentThread();
        rig.Step(eye, Half, 100);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    [Fact]
    public void AnInteriorCutPointDoesNotCount()
    {
        using var uncut = new TileWorldInteriorPickingTests.Rig(false);
        using var cut = new TileWorldInteriorPickingTests.Rig(true);
        Vector3 eye = EnclosedInsideTheField(cut);
        uncut.View.CameraFade = Options(a => a.Id == "tree");
        cut.View.CameraFade = Options(a => a.Id == "tree");

        for (int i = 0; i < 2; i++)
        {
            uncut.View.UpdateCameraFade(eye, Half);
            cut.View.UpdateCameraFade(eye, Half);
        }

        Assert.Equal(1f, uncut.View.CameraFadeOf(uncut.Tree.Id), 4);
        Assert.Equal(0f, cut.View.CameraFadeOf(cut.Tree.Id));
    }

    // A point the greybox tree's own triangles enclose that also lies inside the applied interior field, so the
    // cut is the only thing that can tell the two rigs apart.
    static Vector3 EnclosedInsideTheField(TileWorldInteriorPickingTests.Rig rig)
    {
        TileObjectArchetype tree = rig.Catalogs.Archetype("tree")!;
        Vector3 anchor = TileObjectPlacement.AnchorPosition(rig.Doc, tree, rig.Tree);
        float yaw = TileObjectPlacement.YawRadians(tree, rig.Tree.Rotation);
        (Vector3[] positions, int[] indices) = Flatten(new GreyboxMeshResolver().Resolve(tree)!);
        for (float y = 0.1f; y < 3f; y += 0.1f)
        {
            Vector3 eye = anchor + new Vector3(0f, y, 0f);
            Vector3 local = RayMath.ToObbLocalY(eye, anchor, yaw);
            if (MathF.Abs(MeshContainment.WindingNumber(positions, indices, local)) >= 0.5f
                && rig.View.AppliedInteriorCut!.Contains(eye, rig.Scene.InteriorCutFrame))
                return eye;
        }
        throw new InvalidOperationException("No point both inside the greybox tree and inside the interior field.");
    }

    internal static (Vector3[] Positions, int[] Indices) Flatten(IReadOnlyList<GltfMeshPart> parts)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        foreach (GltfMeshPart part in parts)
        {
            int start = positions.Count;
            foreach (ModelVertex v in part.Mesh.Vertices) positions.Add(v.Position);
            foreach (uint i in part.Mesh.Indices32) indices.Add(start + (int)i);
        }
        return (positions.ToArray(), indices.ToArray());
    }

    internal sealed class ShapeResolver : ITileMeshResolver
    {
        readonly Dictionary<string, IReadOnlyList<GltfMeshPart>> _cache = new(StringComparer.Ordinal);

        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype)
        {
            if (_cache.TryGetValue(archetype.Id, out IReadOnlyList<GltfMeshPart>? cached)) return cached;
            GltfMeshPart[] parts = archetype.Id switch
            {
                "fade_box" => [Part(new Vector3(-1f, 0f, -1f), new Vector3(1f, 4f, 1f))],
                // A canopy block held up by a thin trunk: the box covers the ground under the canopy, the
                // triangles do not.
                "fade_tree" =>
                [
                    Part(new Vector3(-2f, 2f, -2f), new Vector3(2f, 4f, 2f)),
                    Part(new Vector3(-0.1f, 0f, -0.1f), new Vector3(0.1f, 2f, 0.1f)),
                ],
                "fade_stump" => [Part(new Vector3(-0.25f, 0f, -0.25f), new Vector3(0.25f, 0.5f, 0.25f))],
                _ => [Part(new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 1f, 0.5f))],
            };
            _cache[archetype.Id] = parts;
            return parts;
        }

        static GltfMeshPart Part(Vector3 min, Vector3 max) => new(Cuboid(min, max), default);

        // A closed, outward-wound cuboid.
        static GltfMesh Cuboid(Vector3 min, Vector3 max)
        {
            var vertices = new ModelVertex[8];
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y,
                    (i & 4) == 0 ? min.Z : max.Z);
                vertices[i] = new ModelVertex(p, Vector3.UnitY, Vector4.One);
            }
            ushort[] indices =
            [
                0, 1, 5, 0, 5, 4,   // -Y
                2, 6, 7, 2, 7, 3,   // +Y
                0, 4, 6, 0, 6, 2,   // -X
                1, 3, 7, 1, 7, 5,   // +X
                0, 2, 3, 0, 3, 1,   // -Z
                4, 5, 7, 4, 7, 6,   // +Z
            ];
            return new GltfMesh(vertices, indices);
        }
    }
}
