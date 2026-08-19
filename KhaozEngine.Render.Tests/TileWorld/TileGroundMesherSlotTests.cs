using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>What the ground mesher writes for the tile-ground pipeline: the four corner material slots a tile
/// carries, the weights each of its vertices puts over them, and the brightness jitter beside them. Every
/// expectation is hand-computed, the corner rule's counts and its lower-id tie-break included.</summary>
public class TileGroundMesherSlotTests
{
    const ushort Grass = TileRenderTestData.Grass;
    const ushort Dirt = 2;
    const ushort Stone = 3;
    const ushort Wood = TileRenderTestData.WoodFloor;
    const ushort Road = TileRenderTestData.Road;

    static readonly Vector4 OneHotSw = new(1f, 0f, 0f, 0f);

    [Fact]
    public void The_four_slots_are_the_corner_materials_in_SW_SE_NW_NE_order()
    {
        // One road tile with a different material touching each of its corners diagonally. Each corner is then a
        // one-all tie between the road and that diagonal, which the lower id wins, so the four corners come back
        // as the four diagonals: grass south west, dirt south east, stone north west, wood north east. Four
        // distinct answers in four known places is what pins the order.
        TileWorldDocument doc = VoidWorld();
        doc.SetUnderlay(40, 40, 0, Road);
        doc.SetUnderlay(39, 39, 0, Grass);
        doc.SetUnderlay(41, 39, 0, Dirt);
        doc.SetUnderlay(39, 41, 0, Stone);
        doc.SetUnderlay(41, 41, 0, Wood);

        Assert.Equal(Grass, TileGroundMesher.CornerMaterial(doc, 40, 40, 0));
        Assert.Equal(Dirt, TileGroundMesher.CornerMaterial(doc, 41, 40, 0));
        Assert.Equal(Stone, TileGroundMesher.CornerMaterial(doc, 40, 41, 0));
        Assert.Equal(Wood, TileGroundMesher.CornerMaterial(doc, 41, 41, 0));

        List<ModelVertex> tile = TileVertices(Build(doc), doc, 40, 40);
        Assert.Equal(6, tile.Count);
        foreach (ModelVertex v in tile)
        {
            Assert.Equal(SlotOf(Grass), Slot(v.Uv.X));
            Assert.Equal(SlotOf(Dirt), Slot(v.Uv.Y));
            Assert.Equal(SlotOf(Stone), Slot(v.Tangent.X));
            Assert.Equal(SlotOf(Wood), Slot(v.Tangent.Y));
        }

        // And every corner vertex is one-hot on the lane its own corner owns, which is the other half of the
        // order: a mesher that wrote the slots in this order and the weights in another would seam at once.
        Assert.Equal(new Vector4(1f, 0f, 0f, 0f), At(tile, doc, 40f, 40f).Color);
        Assert.Equal(new Vector4(0f, 1f, 0f, 0f), At(tile, doc, 41f, 40f).Color);
        Assert.Equal(new Vector4(0f, 0f, 1f, 0f), At(tile, doc, 40f, 41f).Color);
        Assert.Equal(new Vector4(0f, 0f, 0f, 1f), At(tile, doc, 41f, 41f).Color);
    }

    [Fact]
    public void The_vertex_packs_the_weights_in_colour_and_the_slots_in_uv_and_tangent()
    {
        // The packing the shader reads, pinned field by field on one known vertex: colour is the four weights,
        // uv is the first two slots, tangent is the other two then the jitter then a hard 0.
        TileWorldDocument doc = VoidWorld();
        doc.SetUnderlay(40, 40, 0, Road);
        doc.SetUnderlay(39, 39, 0, Grass);
        doc.SetUnderlay(41, 39, 0, Dirt);
        doc.SetUnderlay(39, 41, 0, Stone);
        doc.SetUnderlay(41, 41, 0, Wood);

        ModelVertex sw = At(TileVertices(Build(doc), doc, 40, 40), doc, 40f, 40f);

        Assert.Equal(TileWorldSpace.ToWorld(40f, 0f, 40f, doc.TileSize), sw.Position);
        Assert.Equal(Vector3.UnitY, sw.Normal);
        Assert.Equal(OneHotSw, sw.Color);
        Assert.Equal(new Vector2(SlotOf(Grass), SlotOf(Dirt)), sw.Uv);
        Assert.Equal(SlotOf(Stone), sw.Tangent.X);
        Assert.Equal(SlotOf(Wood), sw.Tangent.Y);
        Assert.Equal(TileGroundMesher.CornerJitter(doc, 40, 40, 0), sw.Tangent.Z, 1e-6f);
        Assert.Equal(0f, sw.Tangent.W);
    }

    [Theory]
    [InlineData(Stone, Dirt)]
    [InlineData(Dirt, Stone)]
    public void A_two_all_tie_at_a_corner_goes_to_the_lower_material_id(ushort south, ushort north)
    {
        // The corner is shared by four tiles, two carrying one material and two the other, so neither has a
        // majority and the lower id decides. Swapping which pair is which must not change the answer: the corner
        // has to look the same from every tile that touches it or it seams.
        TileWorldDocument doc = VoidWorld();
        doc.SetUnderlay(39, 39, 0, south);
        doc.SetUnderlay(40, 39, 0, south);
        doc.SetUnderlay(39, 40, 0, north);
        doc.SetUnderlay(40, 40, 0, north);

        Assert.Equal(Dirt, TileGroundMesher.CornerMaterial(doc, 40, 40, 0));
    }

    [Fact]
    public void The_most_shared_material_wins_before_the_tie_break_is_reached()
    {
        // Three of the four tiles carry the HIGHER id, so a rule that reached for the lower id first would answer
        // dirt here instead of road.
        TileWorldDocument doc = VoidWorld();
        doc.SetUnderlay(39, 39, 0, Road);
        doc.SetUnderlay(40, 39, 0, Road);
        doc.SetUnderlay(39, 40, 0, Road);
        doc.SetUnderlay(40, 40, 0, Dirt);

        Assert.Equal(Road, TileGroundMesher.CornerMaterial(doc, 40, 40, 0));
    }

    [Fact]
    public void A_void_neighbour_is_not_a_material()
    {
        TileWorldDocument doc = VoidWorld();
        doc.SetUnderlay(40, 40, 0, Road);

        // Three of the four tiles at this corner are void. Counting void would give it a three to one majority
        // and the corner would come back 0.
        Assert.Equal(Road, TileGroundMesher.CornerMaterial(doc, 40, 40, 0));
        // A corner with nothing around it at all has no material, which is the only way 0 is ever the answer. No
        // drawn tile can reach it, because a tile that draws shares all four of its own corners.
        Assert.Equal((ushort)0, TileGroundMesher.CornerMaterial(doc, 10, 10, 0));
    }

    [Fact]
    public void A_NoDraw_neighbour_still_decides_the_corner_material()
    {
        // Void is the ONLY exclusion, exactly as CornerColor excludes it: a NoDraw tile draws no ground of its
        // own but still contributes its underlay, so the ground does not step at the edge of a hole punched for
        // an object floor. Two NoDraw dirt tiles against one grass tile hand the corner to dirt, which a rule
        // that skipped NoDraw would answer grass.
        TileWorldDocument doc = VoidWorld();
        doc.SetUnderlay(39, 39, 0, Dirt);
        doc.SetSettings(39, 39, 0, TileSettings.NoDraw);
        doc.SetUnderlay(39, 40, 0, Dirt);
        doc.SetSettings(39, 40, 0, TileSettings.NoDraw);
        doc.SetUnderlay(40, 40, 0, Grass);

        Assert.Equal(Dirt, TileGroundMesher.CornerMaterial(doc, 40, 40, 0));

        // The grass tile is the only one that draws, and its south west corner names the dirt slot at full weight.
        ModelVertex sw = At(TileVertices(Build(doc), doc, 40, 40), doc, 40f, 40f);
        Assert.Equal(SlotOf(Dirt), Slot(sw.Uv.X));
        Assert.Equal(OneHotSw, sw.Color);
    }

    [Fact]
    public void A_corner_shared_by_two_regions_is_the_same_material_from_both_meshes()
    {
        // Grass west of the region border and dirt east of it, so the border corner is a two-all tie that goes to
        // grass. Both regions have to say so: the west mesh reaches the corner as the east side of its last tile
        // and the east mesh as the west side of its first, which are different lanes of different tiles' slots.
        TileWorldDocument doc = BorderWorld();
        Assert.Equal(Grass, TileGroundMesher.CornerMaterial(doc, TileRegion.Size, 10, 0));

        GltfMesh? westMesh = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, new RegionCoord(0, 0), 0);
        GltfMesh? eastMesh = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, new RegionCoord(1, 0), 0);
        Assert.NotNull(westMesh);
        Assert.NotNull(eastMesh);

        // Three copies on each side: the flat world splits every tile south west to north east, so the corner is
        // in both triangles of the tile that has it as a diagonal end and in one of the tile that does not.
        List<ModelVertex> west = AtCorner(westMesh!, doc, TileRegion.Size, 10);
        List<ModelVertex> east = AtCorner(eastMesh!, doc, 0, 10);
        Assert.Equal(3, west.Count);
        Assert.Equal(3, east.Count);
        foreach (ModelVertex v in west) Assert.Equal(SlotOf(Grass), OneHotSlot(v));
        foreach (ModelVertex v in east) Assert.Equal(SlotOf(Grass), OneHotSlot(v));

        // The jitter has to agree there too, or the two regions meet at a visible brightness step.
        float jitter = TileGroundMesher.CornerJitter(doc, TileRegion.Size, 10, 0);
        foreach (ModelVertex v in west) Assert.Equal(jitter, v.Tangent.Z);
        foreach (ModelVertex v in east) Assert.Equal(jitter, v.Tangent.Z);
    }

    [Theory]
    [InlineData(TileOverlayShape.CornerQuarter)]
    [InlineData(TileOverlayShape.CornerThreeQuarter)]
    public void A_mid_edge_point_splits_its_weight_between_the_two_corners_it_lies_between(TileOverlayShape shape)
    {
        // Rotation 0 cuts the south west corner off, across the south and west mid-edge points. The south one
        // lies between SW and SE, lanes 0 and 1, and the west one between SW and NW, lanes 0 and 2. Either shape
        // emits both, and which of the parts is painted is all that differs between them.
        int x = TileRenderTestData.RoadDiagonalX;
        int z = TileRenderTestData.RoadZ;
        TileWorldDocument doc = CutRoadTile(shape, 0);
        GltfMesh mesh = Build(doc);

        var south = new List<ModelVertex>();
        var westEdge = new List<ModelVertex>();
        foreach (ModelVertex v in TileVertices(mesh, doc, x, z))
        {
            // The painted part carries the overlay's own one-hot weights, so its copies of these two points say
            // nothing about the blend. The blended copies are the ones the ground either side of the cut meets.
            if (v.Color == OneHotSw) continue;
            if (IsAt(v, doc, x + 0.5f, z)) south.Add(v);
            if (IsAt(v, doc, x, z + 0.5f)) westEdge.Add(v);
        }

        Assert.NotEmpty(south);
        Assert.NotEmpty(westEdge);
        foreach (ModelVertex v in south) Assert.Equal(new Vector4(0.5f, 0.5f, 0f, 0f), v.Color);
        foreach (ModelVertex v in westEdge) Assert.Equal(new Vector4(0.5f, 0f, 0.5f, 0f), v.Color);

        // A mid-edge point keeps the tile's four slots: the weights are what changes, never the palette.
        foreach (ModelVertex v in south) Assert.Equal(SlotOf(Grass), Slot(v.Uv.X));

        // And its jitter is the mean of the two corners it lies between, the same averaging the weights get.
        float expected = (TileGroundMesher.CornerJitter(doc, x, z, 0) + TileGroundMesher.CornerJitter(doc, x + 1, z, 0)) * 0.5f;
        foreach (ModelVertex v in south) Assert.Equal(expected, v.Tangent.Z, 1e-6f);
    }

    [Fact]
    public void A_diagonal_half_cut_emits_no_mid_edge_point_at_all()
    {
        // The diagonal split runs corner to corner, so every vertex of both halves is one-hot. The tile-centre
        // case of the weight rule (a quarter on each corner) is unreachable for the same reason: no shape emits a
        // centre point today, and TileTriangulation would have to grow one first.
        TileWorldDocument doc = TileRenderTestData.RoadWorld();
        foreach (ModelVertex v in TileVertices(Build(doc), doc, TileRenderTestData.RoadDiagonalX, TileRenderTestData.RoadZ))
            Assert.Equal(1f, v.Color.X + v.Color.Y + v.Color.Z + v.Color.W, 1e-6f);

        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        foreach (TileOverlayShape shape in Enum.GetValues<TileOverlayShape>())
            for (int rotation = 0; rotation < 4; rotation++)
            {
                int count = TileTriangulation.Triangulate(shape, rotation, rotation % 2 == 0, triangles);
                for (int i = 0; i < count; i++)
                {
                    AssertIsCornerOrMidEdge(triangles[i].A);
                    AssertIsCornerOrMidEdge(triangles[i].B);
                    AssertIsCornerOrMidEdge(triangles[i].C);
                }
            }
    }

    [Fact]
    public void An_overlay_triangle_names_its_own_material_in_every_slot_at_full_weight()
    {
        int x = TileRenderTestData.RoadMinX + 1;
        int z = TileRenderTestData.RoadZ;
        TileWorldDocument doc = TileRenderTestData.RoadWorld();
        List<ModelVertex> tile = TileVertices(Build(doc), doc, x, z);

        Assert.Equal(6, tile.Count);
        foreach (ModelVertex v in tile)
        {
            Assert.Equal(OneHotSw, v.Color);
            Assert.Equal(SlotOf(Road), Slot(v.Uv.X));
            Assert.Equal(SlotOf(Road), Slot(v.Uv.Y));
            Assert.Equal(SlotOf(Road), Slot(v.Tangent.X));
            Assert.Equal(SlotOf(Road), Slot(v.Tangent.Y));
            Assert.Equal(0f, v.Tangent.W);
        }

        // The paint is flat but the brightness is not: an overlay keeps the soft corner-to-corner variation the
        // ground under it has, so its corners carry four different jitters rather than one flat multiplier.
        ModelVertex sw = At(tile, doc, x, z);
        ModelVertex ne = At(tile, doc, x + 1, z + 1);
        Assert.Equal(TileGroundMesher.CornerJitter(doc, x, z, 0), sw.Tangent.Z, 1e-6f);
        Assert.Equal(TileGroundMesher.CornerJitter(doc, x + 1, z + 1, 0), ne.Tangent.Z, 1e-6f);
        Assert.NotEqual(sw.Tangent.Z, ne.Tangent.Z);
    }

    [Fact]
    public void The_jitter_at_a_corner_is_the_mean_over_the_tiles_that_share_it()
    {
        TileWorldDocument doc = GrassRegion();

        // Hand-computed: the four tiles meeting at the corner each hash their own multiplier, and the corner
        // carries their mean, which is what keeps the variation soft instead of stepping at every tile edge.
        float expected = (TileColors.Jitter(39, 39, 0) + TileColors.Jitter(40, 39, 0)
            + TileColors.Jitter(39, 40, 0) + TileColors.Jitter(40, 40, 0)) / 4f;
        Assert.Equal(expected, TileGroundMesher.CornerJitter(doc, 40, 40, 0), 1e-6f);

        GltfMesh mesh = Build(doc);
        Assert.Equal(expected, At(TileVertices(mesh, doc, 40, 40), doc, 40f, 40f).Tangent.Z, 1e-6f);

        // The jitter is a MULTIPLIER, so it is never 0: a vertex carrying 0 renders black however bright the
        // material under it is. The default amplitude keeps it within four percent of 1.
        foreach (ModelVertex v in mesh.Vertices) Assert.InRange(v.Tangent.Z, 0.96f, 1.04f);
    }

    [Fact]
    public void Turning_the_jitter_off_writes_exactly_one_rather_than_zero()
    {
        TileWorldDocument doc = GrassRegion();
        var options = new TileGroundMesherOptions { JitterAmplitude = 0f };

        Assert.Equal(1f, TileGroundMesher.CornerJitter(doc, 40, 40, 0, 0f));
        foreach (ModelVertex v in Build(doc, options).Vertices) Assert.Equal(1f, v.Tangent.Z);
    }

    [Fact]
    public void A_dangling_material_id_lands_on_the_reserved_slot()
    {
        // An id no catalog defines has no layer of its own, and every set keeps one slot for that case so the
        // tile renders the reserved magenta rather than borrowing the look of whatever sits in slot 0.
        TileWorldDocument doc = VoidWorld();
        doc.SetUnderlay(40, 40, 0, 99);
        GltfMesh mesh = Build(doc);

        int missing = IdentitySlotMap.Instance.MissingSlot;
        Assert.Equal(6, mesh.Vertices.Length);
        foreach (ModelVertex v in mesh.Vertices)
        {
            Assert.Equal(missing, Slot(v.Uv.X));
            Assert.Equal(missing, Slot(v.Uv.Y));
            Assert.Equal(missing, Slot(v.Tangent.X));
            Assert.Equal(missing, Slot(v.Tangent.Y));
        }
    }

    [Fact]
    public void Every_slot_comes_from_the_options_slot_map()
    {
        // The stub answers with slots that have nothing to do with the material ids, so a mesher that quietly
        // wrote an id where a slot belongs cannot pass this, on the underlay path or the overlay one.
        TileWorldDocument doc = GrassRegion();
        doc.SetOverlay(41, 40, 0, Road);
        doc.SetOverlayShape(41, 40, 0, TileOverlayShape.Full);
        var options = new TileGroundMesherOptions { Slots = new StubSlotMap() };
        GltfMesh mesh = Build(doc, options);

        foreach (ModelVertex v in TileVertices(mesh, doc, 40, 40)) Assert.Equal(StubSlotMap.GrassSlot, Slot(v.Uv.X));
        foreach (ModelVertex v in TileVertices(mesh, doc, 41, 40)) Assert.Equal(StubSlotMap.RoadSlot, Slot(v.Uv.X));

        // A material the stub does not know lands on its reserved slot. Stone over the whole two by two block
        // that shares the north east corner of the tile, so that corner is stone outright rather than a tie.
        for (int dz = 0; dz <= 1; dz++)
            for (int dx = 0; dx <= 1; dx++)
                doc.SetUnderlay(40 + dx, 40 + dz, 0, Stone);
        ModelVertex ne = At(TileVertices(Build(doc, options), doc, 40, 40), doc, 41f, 41f);
        Assert.Equal(StubSlotMap.Missing, Slot(ne.Tangent.Y));
    }

    [Fact]
    public void The_raycast_still_lands_on_the_triangle_the_mesher_drew()
    {
        // The mesher and the raycast read one triangulation, so changing what a vertex CARRIES must not move
        // where the ground IS. The tile is cut at its south west corner with its south east corner a metre up,
        // and the sample point sits in the (MidS, NE, NW) part, whose plane is 25 cm up there. The uncut pair
        // would put it at 10 cm.
        int x = TileRenderTestData.RoadDiagonalX;
        int z = TileRenderTestData.RoadZ;
        TileWorldDocument doc = CutRoadTile(TileOverlayShape.CornerQuarter, 0);

        var origin = new Vector3(
            TileWorldSpace.WorldX(x + 0.6f, doc.TileSize),
            10f,
            TileWorldSpace.WorldZ(z + 0.5f, doc.TileSize));
        TileHit? hit = TileRaycast.Pick(doc, 0, origin, -Vector3.UnitY);

        Assert.NotNull(hit);
        Assert.Equal(x, hit!.Value.X);
        Assert.Equal(z, hit.Value.Z);
        Assert.Equal(0.25f, hit.Value.Point.Y, 1e-4f);
    }

    // A slot map with no relation to the material ids at all.
    sealed class StubSlotMap : ITileGroundSlotMap
    {
        public const int GrassSlot = 11;
        public const int RoadSlot = 12;
        public const int Missing = 7;

        public int MissingSlot => Missing;

        public int SlotOf(ushort materialId) => materialId switch
        {
            Grass => GrassSlot,
            Road => RoadSlot,
            _ => Missing,
        };
    }

    static GltfMesh Build(TileWorldDocument doc, TileGroundMesherOptions? options = null)
    {
        GltfMesh? mesh = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 0, options);
        Assert.NotNull(mesh);
        return mesh!;
    }

    // The slot the mesher's default identity map holds this material in.
    static int SlotOf(ushort material) => IdentitySlotMap.Instance.SlotOf(material);

    // A slot index out of the float lane carrying it, read the way the shader reads it.
    static int Slot(float lane) => (int)MathF.Floor(lane + 0.5f);

    // The slot the vertex puts all of its weight on, asserting that exactly one lane carries it.
    static int OneHotSlot(ModelVertex v)
    {
        Span<float> weights = stackalloc float[4] { v.Color.X, v.Color.Y, v.Color.Z, v.Color.W };
        Span<float> lanes = stackalloc float[4] { v.Uv.X, v.Uv.Y, v.Tangent.X, v.Tangent.Y };
        int found = -1;
        for (int i = 0; i < 4; i++)
        {
            if (weights[i] == 0f) continue;
            Assert.Equal(1f, weights[i]);
            Assert.Equal(-1, found);
            found = i;
        }
        Assert.NotEqual(-1, found);
        return Slot(lanes[found]);
    }

    // Every triangle whose centroid falls inside the region-local tile, which identifies a tile without depending
    // on the mesher's emission order.
    static List<ModelVertex> TileVertices(GltfMesh mesh, TileWorldDocument doc, int localX, int localZ)
    {
        var found = new List<ModelVertex>();
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            ModelVertex a = mesh.Vertices[mesh.Indices32[t * 3]];
            ModelVertex b = mesh.Vertices[mesh.Indices32[t * 3 + 1]];
            ModelVertex c = mesh.Vertices[mesh.Indices32[t * 3 + 2]];
            Vector3 centre = (a.Position + b.Position + c.Position) / 3f;
            if ((int)MathF.Floor(TileWorldSpace.TileX(centre.X, doc.TileSize)) != localX) continue;
            if ((int)MathF.Floor(TileWorldSpace.TileZ(centre.Z, doc.TileSize)) != localZ) continue;
            found.Add(a);
            found.Add(b);
            found.Add(c);
        }
        return found;
    }

    // The vertex these ones carry at the region-local lattice point, asserting every copy of it agrees on what it
    // carries. Corner and mid-edge points are named in TILE units and converted here, so no call site spells the
    // z flip out.
    static ModelVertex At(List<ModelVertex> vertices, TileWorldDocument doc, float pointX, float pointZ)
    {
        ModelVertex? first = null;
        foreach (ModelVertex v in vertices)
        {
            if (!IsAt(v, doc, pointX, pointZ)) continue;
            if (first is null) { first = v; continue; }
            Assert.Equal(first.Value.Color, v.Color);
            Assert.Equal(first.Value.Uv, v.Uv);
            Assert.Equal(first.Value.Tangent, v.Tangent);
        }
        Assert.True(first.HasValue, $"no vertex at region-local lattice point ({pointX}, {pointZ})");
        return first!.Value;
    }

    // Every vertex of the mesh at one region-local lattice point, however many tiles put a copy there.
    static List<ModelVertex> AtCorner(GltfMesh mesh, TileWorldDocument doc, float pointX, float pointZ)
    {
        var found = new List<ModelVertex>();
        foreach (ModelVertex v in mesh.Vertices)
            if (IsAt(v, doc, pointX, pointZ)) found.Add(v);
        return found;
    }

    static bool IsAt(ModelVertex v, TileWorldDocument doc, float pointX, float pointZ) =>
        MathF.Abs(v.Position.X - TileWorldSpace.WorldX(pointX, doc.TileSize)) < 1e-4f
        && MathF.Abs(v.Position.Z - TileWorldSpace.WorldZ(pointZ, doc.TileSize)) < 1e-4f;

    static void AssertIsCornerOrMidEdge(TileLatticePoint point) =>
        Assert.True(
            Enum.IsDefined(point) && point <= TileLatticePoint.MidW,
            $"{point} is neither a corner nor a mid-edge point, so the mesher has no weights for it");

    // One region with nothing in it, the base the corner-rule cases author their handful of tiles into. A void
    // tile draws nothing, so only the authored ones reach the mesh.
    static TileWorldDocument VoidWorld()
    {
        var doc = new TileWorldDocument { Id = "tile-slot-void", DisplayName = "Slot rule" };
        doc.GetOrCreateRegion(TileRenderTestData.Region);
        return doc;
    }

    // One region of flat grass, for the cases that want a corner with all four of its tiles present.
    static TileWorldDocument GrassRegion()
    {
        TileWorldDocument doc = VoidWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(x, z, 0, Grass);
        return doc;
    }

    // Two regions meeting at x = 64, grass to the west of the border and dirt to the east of it, so the corner
    // column on the border itself is a two-all tie between them.
    static TileWorldDocument BorderWorld()
    {
        var doc = new TileWorldDocument { Id = "tile-slot-border", DisplayName = "Slot border" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.GetOrCreateRegion(new RegionCoord(1, 0));
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size * 2; x++)
                doc.SetUnderlay(x, z, 0, x < TileRegion.Size ? Grass : Dirt);
        return doc;
    }

    // RoadWorld's shaped tile re-cut, with its south east corner raised a metre so the mid-edge point on the
    // south edge sits halfway up a slope rather than on flat ground.
    static TileWorldDocument CutRoadTile(TileOverlayShape shape, int rotation)
    {
        TileWorldDocument doc = TileRenderTestData.RoadWorld();
        int x = TileRenderTestData.RoadDiagonalX;
        int z = TileRenderTestData.RoadZ;
        doc.SetOverlayShape(x, z, 0, shape);
        doc.SetOverlayRotation(x, z, 0, rotation);
        doc.SetCornerHeightCm(x + 1, z, 0, 100);
        return doc;
    }
}
