using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The shared ground triangle rule in <c>KhaozEngine.TileWorld</c> against the ground mesher it was lifted
/// from: for every region-plane, the full-detail positions and indices the rule builds are the mesher's, bit for bit.
/// Feathering stays off, because a feathered overlay adds fade triangles the rule does not describe.</summary>
public sealed class TileGroundTrianglesParityTests
{
    const string Patchwork = "patchwork";
    const string Greybox = "greybox";
    const string River = "river";

    // A dangling underlay id, so the patchwork carries one the catalogs do not define.
    const ushort Dangling = 42;

    // An overlay id the catalogs do not define, which a built material set maps to its reserved slot.
    const ushort UndefinedOverlay = 43;

    // An overlay id at or past the identity map's reserved slot, so that map sends it to the reserved slot too.
    const ushort PastTheIdentityRange = 900;

    [Theory]
    [InlineData(Patchwork)]
    [InlineData(Greybox)]
    [InlineData(River)]
    public void SharedPositionsEqualTheMesherFullDetailPositions(string world)
    {
        TileWorldDocument doc = world switch
        {
            Patchwork => PatchworkWorld(),
            Greybox => TileRenderTestData.GreyboxWorld(),
            River => TileRenderTestData.RiverWorld(),
            _ => throw new ArgumentOutOfRangeException(nameof(world), world, "no such parity world"),
        };
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;

        int triangles = AssertMatchesTheMesher(doc, catalogs, TileGroundMaterials.Build(catalogs));

        Assert.True(triangles > 0, "the world drew no ground");
    }

    // Review focus 1. The mesher keys "overlay present" on the overlay's material slot, and a slot map never answers
    // null for a nonzero id: one it does not carry lands on its reserved slot. So an overlay the catalogs do not
    // define still cuts, under a built material set and under the identity stand-in alike.
    [Fact]
    public void AnOverlayWithoutAMaterialSlotCutsExactlyAsTheMesherDoes()
    {
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        Assert.Null(catalogs.Material(UndefinedOverlay));
        Assert.Null(catalogs.Material(PastTheIdentityRange));
        TileGroundMaterialSet set = TileGroundMaterials.Build(catalogs);
        Assert.Equal(set.MissingSlot, set.SlotOf(UndefinedOverlay));
        Assert.Equal(IdentitySlotMap.Instance.MissingSlot, IdentitySlotMap.Instance.SlotOf(PastTheIdentityRange));

        var doc = new TileWorldDocument { Id = "slotless-overlay", DisplayName = "Slotless overlay" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
            {
                doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
                doc.SetCornerHeightCm(x, z, 0, (short)((x * 37 + z * 53) % 211 - 90));
            }

        ushort[] overlays = [UndefinedOverlay, PastTheIdentityRange, TileRenderTestData.Road, 0];
        TileOverlayShape[] shapes =
        [
            TileOverlayShape.DiagonalHalf, TileOverlayShape.CornerQuarter, TileOverlayShape.CornerThreeQuarter,
        ];
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        int tile = 0;
        foreach (ushort overlay in overlays)
            foreach (TileOverlayShape shape in shapes)
                for (int rotation = 0; rotation < 4; rotation++, tile++)
                {
                    int x = 2 + tile % 16 * 3;
                    int z = 2 + tile / 16 * 3;
                    doc.SetOverlay(x, z, 0, overlay);
                    doc.SetOverlayShape(x, z, 0, shape);
                    doc.SetOverlayRotation(x, z, 0, rotation);

                    // A slotless overlay cuts as the authored shape, and only a missing overlay draws the plain pair.
                    Assert.True(TileGroundTriangles.TryDescribe(doc, catalogs, x, z, 0, out TileGroundCell cell, triangles));
                    Assert.Equal(overlay == 0 ? TileOverlayShape.Full : shape, cell.Cut);
                    Assert.Equal(rotation, cell.Rotation);
                    int cut = shape == TileOverlayShape.DiagonalHalf ? 2 : 4;
                    Assert.Equal(overlay == 0 ? 2 : cut, cell.TriangleCount);
                }

        Assert.True(AssertMatchesTheMesher(doc, catalogs, set) > 0);
        Assert.True(AssertMatchesTheMesher(doc, catalogs, IdentitySlotMap.Instance) > 0);
    }

    // Review focus 2. Region origins below zero, a region with no neighbour on any side whose border corners are
    // edge-extended reads, a cluster whose borders read each other, a tile size other than one metre, and a plane
    // above the ground whose heights derive from plane 0 plus its lift.
    [Fact]
    public void NegativeAndEdgeRegionsMatchTheMesher()
    {
        var doc = new TileWorldDocument { Id = "edges", DisplayName = "Edges", TileSize = 1.37f };
        RegionCoord[] regions =
        [
            new(-1, -1), new(0, -1), new(-1, 0), new(0, 0), new(-4, 3), new(3, -5),
        ];
        var rng = new Random(20261001);
        foreach (RegionCoord region in regions)
        {
            doc.GetOrCreateRegion(region);
            for (int lz = 0; lz < TileRegion.Size; lz++)
                for (int lx = 0; lx < TileRegion.Size; lx++)
                {
                    int x = region.OriginX + lx;
                    int z = region.OriginZ + lz;
                    doc.SetCornerHeightCm(x, z, 0, (short)rng.Next(-300, 301));
                    doc.SetUnderlay(x, z, 0, (ushort)(1 + ((x * 7 + z * 3) % 5 + 5) % 5));
                    bool edge = lx is 0 or TileRegion.Size - 1 || lz is 0 or TileRegion.Size - 1;
                    int roll = rng.Next(0, 100);
                    if (edge && roll < 40)
                    {
                        doc.SetOverlay(x, z, 0, roll < 30 ? TileRenderTestData.Road : UndefinedOverlay);
                        doc.SetOverlayShape(x, z, 0, (TileOverlayShape)(roll % 4));
                        doc.SetOverlayRotation(x, z, 0, rng.Next(0, 4));
                    }
                    else if (edge && roll < 45)
                    {
                        doc.SetSettings(x, z, 0, TileSettings.NoDraw);
                    }
                    else if (roll < 3)
                    {
                        doc.SetUnderlay(x, z, 0, 0);
                    }
                    if ((lx + lz) % 9 == 0) doc.SetUnderlay(x, z, 1, TileRenderTestData.WoodFloor);
                }
        }

        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        Assert.True(AssertMatchesTheMesher(doc, catalogs, TileGroundMaterials.Build(catalogs)) > 0);
    }

    // Builds every region-plane both ways and compares the raw position and index memory, so a sign of zero or a
    // last bit of rounding counts. Returns how many triangles the world drew, so a caller can refuse a vacuous pass.
    static int AssertMatchesTheMesher(TileWorldDocument doc, TileWorldCatalogs catalogs, ITileGroundSlotMap slots)
    {
        var options = new TileGroundMesherOptions { Slots = slots };
        int triangles = 0;
        foreach (RegionCoord region in doc.Regions.Keys.OrderBy(r => r.Rz).ThenBy(r => r.Rx))
            for (int plane = 0; plane < doc.PlaneCount; plane++)
            {
                GltfMesh? mesher = TileGroundMesher.Build(doc, catalogs, region, plane, TileGroundLod.Full, options);
                Vector3[] expectedPositions = mesher?.Vertices.Select(v => v.Position).ToArray() ?? [];
                uint[] expectedIndices = mesher?.Indices32 ?? [];

                TileGroundMesh shared = TileGroundTriangles.Build(doc, catalogs, region, plane);

                Assert.Equal(region, shared.Region);
                Assert.Equal(plane, shared.Plane);
                Assert.Equal(expectedPositions.Length, shared.Positions.Length);
                Assert.True(
                    MemoryMarshal.AsBytes(expectedPositions.AsSpan())
                        .SequenceEqual(MemoryMarshal.AsBytes(shared.Positions.AsSpan())),
                    $"positions differ in region {region} plane {plane}");
                Assert.True(
                    MemoryMarshal.AsBytes(expectedIndices.AsSpan())
                        .SequenceEqual(MemoryMarshal.AsBytes(shared.Indices.AsSpan())),
                    $"indices differ in region {region} plane {plane}");
                triangles += shared.Indices.Length / 3;
            }
        return triangles;
    }

    // The golden tests' patchwork without its feathered block: nine regions of rolling ground under a three-material
    // patchwork, with every overlay shape, NoDraw holes and a dangling id scattered through them. Copied rather than
    // shared so neither file's input can drift with the other's.
    static TileWorldDocument PatchworkWorld()
    {
        var doc = new TileWorldDocument { Id = "ground-parity", DisplayName = "Ground parity" };
        for (int rz = 0; rz < 3; rz++)
            for (int rx = 0; rx < 3; rx++)
                doc.GetOrCreateRegion(new RegionCoord(rx, rz));

        var rng = new Random(20260923);
        int size = 3 * TileRegion.Size;
        for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++)
            {
                doc.SetCornerHeightCm(x, z, 0,
                    (short)(120 * Math.Sin(x * 0.11) * Math.Cos(z * 0.07) + rng.Next(-15, 16)));
                ushort underlay = (ushort)((x / 7 + z / 5 + rng.Next(0, 2)) % 3 + 1);
                doc.SetUnderlay(x, z, 0, rng.Next(0, 200) == 0 ? Dangling : underlay);
                int roll = rng.Next(0, 100);
                if (roll < 6)
                {
                    doc.SetOverlay(x, z, 0, TileRenderTestData.Road);
                    doc.SetOverlayShape(x, z, 0, TileOverlayShape.Full);
                }
                else if (roll < 12)
                {
                    doc.SetOverlay(x, z, 0, TileRenderTestData.Road);
                    doc.SetOverlayShape(x, z, 0, (TileOverlayShape)(1 + roll % 3));
                    doc.SetOverlayRotation(x, z, 0, rng.Next(0, 4));
                }
                else if (roll < 13)
                {
                    doc.SetSettings(x, z, 0, TileSettings.NoDraw);
                }
            }
        return doc;
    }
}
