using System;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The ground mesher computes each lattice corner's height, normal, jitter and slot once per build and
/// shares it between the tiles meeting there. The memo is only allowed to save work, never to change a byte, so every
/// mesh here is built twice, memoised and direct, and compared as raw vertex and index memory.</summary>
public sealed class TileGroundCornerCacheTests
{
    // A dangling underlay id, so the missing-slot answer is memoised along with the real ones.
    const ushort Dangling = 42;

    // Nine regions of rolling ground under a three-material patchwork, with every overlay shape, NoDraw holes and a
    // dangling id scattered through them. Every corner answer the mesher asks for varies, and the centre region reads
    // a neighbour on every side.
    static TileWorldDocument Patchwork()
    {
        var doc = new TileWorldDocument { Id = "corner-memo", DisplayName = "Corner memo" };
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

    public static TheoryData<bool, float> Settings => new()
    {
        { true, TileColors.DefaultJitterAmplitude },
        { false, TileColors.DefaultJitterAmplitude },
        { true, 0f },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void Memoised_corners_build_the_direct_path_mesh_bit_for_bit(bool smoothNormals, float jitter)
    {
        TileWorldDocument doc = Patchwork();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        var options = new TileGroundMesherOptions
        {
            SmoothNormals = smoothNormals,
            JitterAmplitude = jitter,
            Slots = TileGroundMaterials.Build(catalogs),
        };

        // The centre reads a neighbour on every side, and a corner region edge-extends on its outer two.
        foreach (RegionCoord region in new[] { new RegionCoord(1, 1), new RegionCoord(0, 0), new RegionCoord(2, 1) })
            foreach (TileGroundLod lod in new[] { TileGroundLod.Full, TileGroundLod.Coarse4 })
                AssertSameBytes(doc, catalogs, region, lod, options);
    }

    [Fact]
    public void Memoised_corners_match_on_the_greybox_worlds_coarse_cells_too()
    {
        // Mostly flat single-material ground, which is what turns Coarse4 cells compatible and gives the canonical
        // transition points and the coarse corners a real share of the mesh. The patchwork above is too busy for it.
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        var options = new TileGroundMesherOptions { Slots = TileGroundMaterials.Build(catalogs) };
        foreach (TileWorldDocument doc in new[] { TileRenderTestData.GreyboxWorld(), TileRenderTestData.RiverWorld() })
            foreach (TileGroundLod lod in new[] { TileGroundLod.Full, TileGroundLod.Coarse4 })
                AssertSameBytes(doc, catalogs, TileRenderTestData.Region, lod, options);
    }

    static void AssertSameBytes(TileWorldDocument doc, TileWorldCatalogs catalogs, RegionCoord region,
                                TileGroundLod lod, TileGroundMesherOptions options)
    {
        GltfMesh? memoised = TileGroundMesher.Build(doc, catalogs, region, 0, lod, options, memoiseCorners: true);
        GltfMesh? direct = TileGroundMesher.Build(doc, catalogs, region, 0, lod, options, memoiseCorners: false);

        Assert.NotNull(memoised);
        Assert.NotNull(direct);
        Assert.Equal(direct.Vertices.Length, memoised.Vertices.Length);
        Assert.True(
            MemoryMarshal.AsBytes(direct.Vertices.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(memoised.Vertices.AsSpan())),
            $"region {region} at {lod}: the memoised vertices differ from the direct path's");
        Assert.Equal(direct.Indices32, memoised.Indices32);
    }

    [Fact]
    public void The_public_build_matches_the_direct_path()
    {
        TileWorldDocument doc = Patchwork();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        var region = new RegionCoord(1, 1);

        GltfMesh? viaPublic = TileGroundMesher.Build(doc, catalogs, region, 0);
        GltfMesh? direct = TileGroundMesher.Build(doc, catalogs, region, 0, TileGroundLod.Full, options: null,
                                                  memoiseCorners: false);

        Assert.NotNull(viaPublic);
        Assert.NotNull(direct);
        Assert.True(MemoryMarshal.AsBytes(direct.Vertices.AsSpan())
            .SequenceEqual(MemoryMarshal.AsBytes(viaPublic.Vertices.AsSpan())));
    }
}
