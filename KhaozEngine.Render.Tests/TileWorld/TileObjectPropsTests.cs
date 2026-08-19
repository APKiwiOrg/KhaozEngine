using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The object-to-prop pass: the clockwise yaw convention, the footprint-centre anchor, the roof split,
/// and the greybox mesh resolver that stands in for real archetype meshes.</summary>
public class TileObjectPropsTests
{
    const float Tolerance = 1e-4f;

    static TileObjectArchetype Archetype(string id) => TileRenderTestData.Catalogs.Archetype(id)!;

    // The convention test the whole yaw sign hangs off: a mesh point on the WEST side of the tile centre has to
    // land on the NORTH side after one quarter turn, because rotation counts quarter turns clockwise from above.
    // North is -world z, so the landing point is (0, 0, -0.5) and the yaw that gets there is NEGATIVE.
    [Fact]
    public void Rotation_1_turns_a_west_edge_point_to_the_north_edge()
    {
        float yaw = TileObjectProps.YawRadians(Archetype("wall"), 1);
        Vector3 turned = Vector3.Transform(new Vector3(-0.5f, 0f, 0f), Matrix4x4.CreateRotationY(yaw));
        Assert.Equal(0f, turned.X, Tolerance);
        Assert.Equal(0f, turned.Y, Tolerance);
        Assert.Equal(-0.5f, turned.Z, Tolerance);
        Assert.True(yaw < 0f, "negative yaw is what turns clockwise viewed from above with north up");
    }

    [Theory]
    [InlineData(0, -0.5f, 0f)]    // west stays west
    [InlineData(1, 0f, -0.5f)]    // west turns north, which is -z
    [InlineData(2, 0.5f, 0f)]     // west turns east
    [InlineData(3, 0f, 0.5f)]     // west turns south, which is +z
    public void Every_rotation_turns_the_west_edge_point_clockwise(int rotation, float x, float z)
    {
        float yaw = TileObjectProps.YawRadians(Archetype("wall"), rotation);
        Vector3 turned = Vector3.Transform(new Vector3(-0.5f, 0f, 0f), Matrix4x4.CreateRotationY(yaw));
        Assert.Equal(x, turned.X, Tolerance);
        Assert.Equal(z, turned.Z, Tolerance);
    }

    [Fact]
    public void The_archetype_yaw_offset_adds_to_the_rotation()
    {
        var offset = new TileObjectArchetype { Id = "offset", YawOffsetDegrees = 90f };
        Assert.Equal(
            TileObjectProps.YawRadians(Archetype("wall"), 2),
            TileObjectProps.YawRadians(offset, 1),
            Tolerance);
    }

    [Fact]
    public void A_wall_at_rotation_1_sits_at_the_tile_centre_with_the_north_yaw()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        TileRegionProps props = TileObjectProps.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 0);

        // The middle of the north run, which is the one wall on its tile: the corner tiles carry a side wall too
        // and the tile south of it is the doorway.
        PropPlacement wall = props.Ground.Single(p =>
            p.Id == "wall" &&
            p.X == TileWorldSpace.WorldX(TileRenderTestData.HouseDoorX + 0.5f, doc.TileSize) &&
            p.Z == TileWorldSpace.WorldZ(TileRenderTestData.HouseMaxZ + 0.5f, doc.TileSize));

        // Assert the north yaw directly rather than against YawRadians, so this test still fails if the whole
        // convention flips sign underneath both of them.
        Vector3 turned = Vector3.Transform(new Vector3(-0.5f, 0f, 0f), Matrix4x4.CreateRotationY(wall.Yaw));
        Assert.Equal(0f, turned.X, Tolerance);
        Assert.Equal(-0.5f, turned.Z, Tolerance);

        Assert.Equal(0f, wall.Y, Tolerance);
        Assert.Equal(1f, wall.Scale, Tolerance);
        Assert.Equal(0, wall.Variant);
    }

    [Fact]
    public void Two_by_two_rock_centres_on_the_footprint()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        doc.AddObject("rock_large", 10, 10, 0, 0);

        TileRegionProps props = TileObjectProps.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 0);
        PropPlacement rock = props.Ground.Single(p => p.Id == "rock_large");

        Assert.Equal(TileWorldSpace.WorldX(11f, doc.TileSize), rock.X, Tolerance);
        Assert.Equal(TileWorldSpace.WorldZ(11f, doc.TileSize), rock.Z, Tolerance);
        Assert.Equal(-11f, rock.Z, Tolerance);
    }

    [Fact]
    public void An_odd_rotation_swaps_the_footprint_before_centring()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        doc.AddObject("bench", 20, 20, 0, 1);   // bench is 1 wide by 2 deep, so rotation 1 makes it 2 by 1

        TileRegionProps props = TileObjectProps.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 0);
        PropPlacement bench = props.Ground.Single(p => p.Id == "bench");

        Assert.Equal(TileWorldSpace.WorldX(21f, doc.TileSize), bench.X, Tolerance);
        Assert.Equal(TileWorldSpace.WorldZ(20.5f, doc.TileSize), bench.Z, Tolerance);
    }

    [Fact]
    public void Positions_scale_with_the_tile_size()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        doc.TileSize = 2f;
        doc.AddObject("rock_large", 10, 10, 0, 0);

        TileRegionProps props = TileObjectProps.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 0);
        PropPlacement rock = props.Ground.Single(p => p.Id == "rock_large");

        Assert.Equal(22f, rock.X, Tolerance);
        Assert.Equal(-22f, rock.Z, Tolerance);
    }

    [Fact]
    public void The_anchor_takes_its_height_from_the_document()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.AddObject("tree", TileRenderTestData.HillMin, TileRenderTestData.HillMin, 0, 0);

        TileRegionProps props = TileObjectProps.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 0);
        PropPlacement tree = props.Ground.Single(p => p.Id == "tree");

        Assert.Equal(doc.HeightAt(tree.X, tree.Z, 0), tree.Y, Tolerance);
        Assert.True(tree.Y > 0f, "the tree stands on the raised block, so its anchor is above zero");
    }

    [Fact]
    public void The_anchor_height_is_sampled_in_world_units()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.TileSize = 2f;
        doc.AddObject("tree", TileRenderTestData.HillMin, TileRenderTestData.HillMin, 0, 0);

        TileRegionProps props = TileObjectProps.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 0);
        PropPlacement tree = props.Ground.Single(p => p.Id == "tree");

        // HeightAt takes WORLD units and converts them itself, so the anchor has to hand it the scaled centre.
        // Anchoring inside the raised block separates the two readings: the world-unit centre (41, -41) comes
        // back as tile (20.5, 20.5) and finds the hill, while the unscaled tile centre would be scaled again
        // down to tile 10.25 and find flat ground.
        Assert.Equal(41f, tree.X, Tolerance);
        Assert.Equal(-41f, tree.Z, Tolerance);
        Assert.Equal(TileRenderTestData.HillHeightCm / 100f, tree.Y, Tolerance);
        Assert.Equal(0f, doc.HeightAt(20.5f, -20.5f, 0), Tolerance);
    }

    [Fact]
    public void Roof_archetypes_go_to_the_roofs_list()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;

        TileRegionProps roofPlane = TileObjectProps.Build(doc, catalogs, TileRenderTestData.Region, TileRenderTestData.RoofPlane);
        Assert.Empty(roofPlane.Ground);
        Assert.NotEmpty(roofPlane.Roofs);
        Assert.All(roofPlane.Roofs, p => Assert.Equal("roof_flat", p.Id));

        TileRegionProps groundPlane = TileObjectProps.Build(doc, catalogs, TileRenderTestData.Region, 0);
        Assert.Empty(groundPlane.Roofs);
        Assert.NotEmpty(groundPlane.Ground);
        Assert.DoesNotContain(groundPlane.Ground, p => p.Id == "roof_flat");
    }

    [Fact]
    public void Objects_on_other_planes_and_unknown_archetypes_are_skipped()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        doc.AddObject("not_in_the_catalogs", 20, 20, 0, 0);

        TileRegionProps props = TileObjectProps.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 0);

        Assert.DoesNotContain(props.Ground, p => p.Id == "not_in_the_catalogs");
        Assert.DoesNotContain(props.Roofs, p => p.Id == "not_in_the_catalogs");
        Assert.DoesNotContain(props.Ground, p => p.Id == "roof_flat");   // roof_flat lives on plane 1
    }

    [Fact]
    public void A_region_the_document_does_not_hold_builds_empty_lists()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        TileRegionProps props = TileObjectProps.Build(doc, TileRenderTestData.Catalogs, new RegionCoord(7, 7), 0);
        Assert.Empty(props.Ground);
        Assert.Empty(props.Roofs);
    }

    [Fact]
    public void Greybox_resolver_returns_a_box_per_archetype_with_the_footprint_extent()
    {
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        var resolver = new GreyboxMeshResolver();

        foreach (TileObjectArchetype a in catalogs.Archetypes.Values)
        {
            IReadOnlyList<GltfMeshPart>? parts = resolver.Resolve(a);
            GltfMeshPart part = Assert.Single(parts!);
            Assert.NotEmpty(part.Mesh.Vertices);
            Assert.Equal(0, part.Mesh.Indices32.Length % 3);
            Assert.True(part.Maps.IsEmpty, $"'{a.Id}' is a vertex-coloured greybox box, so it carries no textures");

            float halfX = a.SizeX * 0.5f, halfZ = a.SizeZ * 0.5f;
            foreach (ModelVertex v in part.Mesh.Vertices)
            {
                Assert.InRange(v.Position.X, -halfX - Tolerance, halfX + Tolerance);
                Assert.InRange(v.Position.Z, -halfZ - Tolerance, halfZ + Tolerance);
                Assert.True(v.Position.Y >= -Tolerance, $"'{a.Id}' sits on or above its anchor");
                Assert.Equal(1f, v.Color.W, Tolerance);
            }
        }

        // The full-footprint case fills its rect exactly: rock_large is 2 by 2, so it spans -1..1 in x and z.
        GltfMesh rock = resolver.Resolve(catalogs.Archetype("rock_large")!)![0].Mesh;
        Assert.Equal(-1f, rock.Vertices.Min(v => v.Position.X), Tolerance);
        Assert.Equal(1f, rock.Vertices.Max(v => v.Position.X), Tolerance);
        Assert.Equal(-1f, rock.Vertices.Min(v => v.Position.Z), Tolerance);
        Assert.Equal(1f, rock.Vertices.Max(v => v.Position.Z), Tolerance);
        Assert.Equal(1f, rock.Vertices.Max(v => v.Position.Y), Tolerance);
    }

    [Fact]
    public void The_greybox_wall_is_a_thin_slab_on_the_west_edge()
    {
        GltfMesh wall = new GreyboxMeshResolver().Resolve(Archetype("wall"))![0].Mesh;

        Assert.Equal(-0.5f, wall.Vertices.Min(v => v.Position.X), Tolerance);
        Assert.Equal(-0.35f, wall.Vertices.Max(v => v.Position.X), Tolerance);
        Assert.Equal(-0.5f, wall.Vertices.Min(v => v.Position.Z), Tolerance);
        Assert.Equal(0.5f, wall.Vertices.Max(v => v.Position.Z), Tolerance);
        Assert.Equal(TileWorldDocument.DefaultPlaneHeight, wall.Vertices.Max(v => v.Position.Y), Tolerance);
    }

    // The rule the roof lift was: a mesh lives in ITS OWN plane's local space, so a roof placed on the plane
    // above the walls it covers starts at y 0 like everything else. Building it at wall height instead put the
    // slab a whole plane above the walls once the anchor added that plane's own lift.
    [Fact]
    public void The_greybox_roof_slab_sits_on_its_own_plane_floor()
    {
        GltfMesh roof = new GreyboxMeshResolver().Resolve(Archetype("roof_flat"))![0].Mesh;

        Assert.Equal(0f, roof.Vertices.Min(v => v.Position.Y), Tolerance);
        Assert.Equal(GreyboxMeshResolver.RoofThickness, roof.Vertices.Max(v => v.Position.Y), Tolerance);
    }

    [Fact]
    public void A_greybox_wall_is_as_tall_as_the_wall_height_it_was_given()
    {
        var resolver = new GreyboxMeshResolver(TileWorldDocument.DefaultTileSize, 4.25f);

        Assert.Equal(4.25f, resolver.WallHeight, Tolerance);
        foreach (string id in new[] { "wall", "wall_corner", "diag_wall" })
        {
            GltfMesh mesh = resolver.Resolve(Archetype(id))![0].Mesh;
            Assert.Equal(4.25f, mesh.Vertices.Max(v => v.Position.Y), Tolerance);
            Assert.Equal(0f, mesh.Vertices.Min(v => v.Position.Y), Tolerance);
        }
    }

    // The default is the plane height, because a wall has to span exactly one plane to meet the roof above it.
    [Fact]
    public void The_default_greybox_wall_height_is_one_plane()
    {
        Assert.Equal(TileWorldDocument.DefaultPlaneHeight, new GreyboxMeshResolver().WallHeight, Tolerance);
    }

    [Fact]
    public void The_greybox_corner_wall_is_two_slabs_meeting_at_the_north_west_corner()
    {
        GltfMesh corner = new GreyboxMeshResolver().Resolve(Archetype("wall_corner"))![0].Mesh;

        // Two boxes, so twice a box's 24 vertices and 36 indices.
        Assert.Equal(48, corner.Vertices.Length);
        Assert.Equal(72, corner.Indices32.Length);
        // Together the two slabs fill the footprint's full extent on both axes: the west one runs the full depth
        // and the north one the full width.
        Assert.Equal(-0.5f, corner.Vertices.Min(v => v.Position.X), Tolerance);
        Assert.Equal(0.5f, corner.Vertices.Max(v => v.Position.X), Tolerance);
        Assert.Equal(-0.5f, corner.Vertices.Min(v => v.Position.Z), Tolerance);
        Assert.Equal(0.5f, corner.Vertices.Max(v => v.Position.Z), Tolerance);

        // The two slabs are the only geometry here, so every vertex east of the west slab's inner face belongs to
        // the north one. That is what pins the north edge at world -z: with north on +z they would all sit at the
        // far side instead, and the corner would read as south west.
        float inner = -0.5f + GreyboxMeshResolver.WallThickness;
        Assert.All(
            corner.Vertices.Where(v => v.Position.X > inner + Tolerance),
            v => Assert.True(v.Position.Z <= inner + Tolerance,
                $"a north slab vertex should sit at the -z edge, not z {v.Position.Z}"));
    }

    [Fact]
    public void The_greybox_tree_is_a_narrow_three_metre_box()
    {
        GltfMesh tree = new GreyboxMeshResolver().Resolve(Archetype("tree"))![0].Mesh;

        Assert.Equal(-0.3f, tree.Vertices.Min(v => v.Position.X), Tolerance);
        Assert.Equal(0.3f, tree.Vertices.Max(v => v.Position.X), Tolerance);
        Assert.Equal(3f, tree.Vertices.Max(v => v.Position.Y), Tolerance);
    }

    [Fact]
    public void The_greybox_resolver_scales_its_boxes_by_the_tile_size()
    {
        GltfMesh rock = new GreyboxMeshResolver(2f).Resolve(Archetype("rock_large"))![0].Mesh;

        Assert.Equal(-2f, rock.Vertices.Min(v => v.Position.X), Tolerance);
        Assert.Equal(2f, rock.Vertices.Max(v => v.Position.Z), Tolerance);
    }

    [Fact]
    public void The_greybox_colour_is_stable_per_archetype_and_differs_between_them()
    {
        var resolver = new GreyboxMeshResolver();
        Vector4 wall = GreyboxMeshResolver.ColorOf("wall");

        Assert.Equal(wall, GreyboxMeshResolver.ColorOf("wall"));
        Assert.NotEqual(wall, GreyboxMeshResolver.ColorOf("tree"));
        Assert.All(resolver.Resolve(Archetype("wall"))![0].Mesh.Vertices, v => Assert.Equal(wall, v.Color));
    }

    [Fact]
    public void The_greybox_resolver_hands_back_the_same_parts_for_the_same_archetype()
    {
        var resolver = new GreyboxMeshResolver();
        TileObjectArchetype tree = Archetype("tree");
        Assert.Same(resolver.Resolve(tree)![0].Mesh, resolver.Resolve(tree)![0].Mesh);
    }

    [Fact]
    public void Box_builds_twelve_triangles_with_outward_face_normals()
    {
        GltfMesh box = GreyboxMeshResolver.Box(Vector3.Zero, Vector3.One, Vector4.One);

        Assert.Equal(24, box.Vertices.Length);
        Assert.Equal(12, box.TriangleCount);

        var normals = box.Vertices.Select(v => v.Normal).Distinct().ToList();
        Assert.Equal(6, normals.Count);
        Assert.All(normals, n => Assert.Equal(1f, n.Length(), Tolerance));

        // Every face normal points away from the box centre.
        foreach (ModelVertex v in box.Vertices)
            Assert.True(Vector3.Dot(v.Normal, v.Position - new Vector3(0.5f, 0.5f, 0.5f)) > 0f);
    }
}
