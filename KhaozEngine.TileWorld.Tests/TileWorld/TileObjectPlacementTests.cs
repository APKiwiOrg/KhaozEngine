using System;
using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The placement moved out of the renderer into the document package. Every number here is compared BIT FOR
/// BIT against the formula the renderer carried before the move, written out again below, so a refactor of the
/// helper that nudged a single float would move every prop, every model pick and every walk surface at once.</summary>
public class TileObjectPlacementTests
{
    // The rule exactly as TileObjectProps held it before the move.
    static float YawBefore(TileObjectArchetype archetype, int rotation) =>
        -(rotation * 90f + archetype.YawOffsetDegrees) * (MathF.PI / 180f);

    static Vector3 AnchorBefore(TileWorldDocument doc, TileObjectArchetype archetype, TileObject o)
    {
        (int sizeX, int sizeZ) = TileFootprint.Rotated(archetype, o.Rotation);
        float cx = TileWorldSpace.WorldX(o.X + sizeX / 2f, doc.TileSize);
        float cz = TileWorldSpace.WorldZ(o.Z + sizeZ / 2f, doc.TileSize);
        return new Vector3(cx, doc.HeightAt(cx, cz, o.Plane), cz);
    }

    static TileWorldDocument Slope(float tileSize)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.TileSize = tileSize;
        for (int z = 0; z <= 16; z++)
            for (int x = 0; x <= 16; x++)
                doc.SetCornerHeightCm(x, z, 0, (short)(x * 37 - z * 11));
        return doc;
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(1f, 37.5f)]
    [InlineData(2.5f, -90f)]
    [InlineData(0.75f, 12.25f)]
    public void Anchor_yaw_and_matrix_match_the_renderer_rule_bit_for_bit(float tileSize, float yawOffset)
    {
        TileWorldDocument doc = Slope(tileSize);
        var archetype = new TileObjectArchetype { Id = "bench", SizeX = 1, SizeZ = 3, YawOffsetDegrees = yawOffset };
        foreach (int rotation in new[] { 0, 1, 2, 3, 5, -1 })
        {
            foreach (int plane in new[] { 0, 2 })
            {
                var o = new TileObject { Id = 1, ArchetypeId = "bench", X = 4, Z = 7, Plane = plane, Rotation = rotation };

                Vector3 anchor = TileObjectPlacement.AnchorPosition(doc, archetype, o);
                float yaw = TileObjectPlacement.YawRadians(archetype, rotation);

                Assert.Equal(AnchorBefore(doc, archetype, o), anchor);
                Assert.Equal(YawBefore(archetype, rotation), yaw);
                Assert.Equal(
                    Matrix4x4.CreateRotationY(YawBefore(archetype, rotation))
                        * Matrix4x4.CreateTranslation(AnchorBefore(doc, archetype, o)),
                    TileObjectPlacement.LocalToWorld(doc, archetype, o));
            }
        }
    }

    // The query's own basis is the drawn rotation with exact axes on a quarter turn. It must agree with the matrix to
    // well under a millimetre on any turn, or the deck the query answers is not the deck on screen.
    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(45f)]
    [InlineData(-22.5f)]
    public void The_planar_basis_is_the_drawn_rotation(float yawOffset)
    {
        var archetype = new TileObjectArchetype { Id = "a", YawOffsetDegrees = yawOffset };
        for (int rotation = -1; rotation <= 5; rotation++)
        {
            Matrix4x4 drawn = Matrix4x4.CreateRotationY(TileObjectPlacement.YawRadians(archetype, rotation));
            TileObjectPlacement.PlanarBasis(archetype, rotation, out float cos, out float sin);
            var local = new Vector3(2.5f, 0f, -1.5f);
            Vector3 expected = Vector3.Transform(local, drawn);
            Assert.Equal(expected.X, local.X * cos + local.Z * sin, 1e-5f);
            Assert.Equal(expected.Z, local.Z * cos - local.X * sin, 1e-5f);
        }
    }

    [Fact]
    public void A_quarter_turn_basis_is_exact()
    {
        var archetype = new TileObjectArchetype { Id = "a" };
        TileObjectPlacement.PlanarBasis(archetype, 1, out float cos, out float sin);
        Assert.Equal((0f, -1f), (cos, sin));
        archetype.YawOffsetDegrees = 180f;
        TileObjectPlacement.PlanarBasis(archetype, 1, out cos, out sin);
        Assert.Equal((0f, 1f), (cos, sin));
    }
}
