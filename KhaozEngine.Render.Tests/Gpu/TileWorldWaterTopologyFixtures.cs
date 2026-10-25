using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.Tests.TileWorld;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Which wrong topology a case's negative witness renders and samples. Each one is a surface a
/// producer could plausibly have used instead of the drawn triangles.</summary>
internal enum TileWaterTopologyWitness
{
    /// <summary>Two triangles per tile on the diagonal the drawn tile did not use.</summary>
    OtherDiagonal,
    /// <summary>Two triangles per tile on the corner-height rule, ignoring authored shape and rotation.</summary>
    HeightRuleSplit,
    /// <summary>The Full-detail mesh of every loaded region, where the drawn ground is Coarse4.</summary>
    FullDetail,
    /// <summary>The drawn triangles bound one metre east of their true origin, a wrong origin reduction.</summary>
    ShiftedBinding,
}

/// <summary>One public synthetic topology case. Loads run in order. A null residency uses the synchronous
/// Full LoadRegion path. Probes are extra world XZ points beyond the regular lattice. Ground colour jitter is a
/// hash of absolute tile coordinates, so a translated pair only renders the same terrain without it.</summary>
internal sealed record TileWaterTopologyCase(
    string Name,
    Func<TileWorldDocument> Build,
    (RegionCoord Region, TileRegionResidencyState? Residency)[] Loads,
    Vector3 Eye,
    Vector3 Target,
    float FarPlane,
    int ExpectedFields,
    TileWaterTopologyWitness Witness,
    float LatticeSpacing,
    Vector2[] Probes,
    float JitterAmplitude = TileColors.DefaultJitterAmplitude)
{
    public Vector3 Focus => new(Target.X, 0f, Target.Z);
    public bool Profiled => Array.Exists(Loads, load => load.Residency is not null);
}

/// <summary>Public synthetic documents for triangle-backed water topology acceptance. Grass banks surround
/// every body and every region its certified domain touches is loaded. Heights are whole centimetres, so
/// mid-edge and centre lattice points are exact binary32 values. No consumer content is used.</summary>
internal static class TileWorldWaterTopologyFixtures
{
    /// <summary>The translation of the distant case: 32 regions east, 2048 m, a multiple of the 128 m
    /// render-origin grid, so the reduced frame is the base frame exactly.</summary>
    internal const int DistantRegionOffset = 32;

    internal static readonly TileWaterTopologyCase OtherDiagonal = new("nw-se-ridge", NwSeRidge,
        [(new RegionCoord(0, 0), null)], new(10.5f, 1.8f, -9.7f), new(10.5f, 0f, -10.5f), 20f, 1,
        TileWaterTopologyWitness.OtherDiagonal, 0.125f, [new(10.5f, -10.58f), new(10.5f, -10.42f)]);

    internal static readonly TileWaterTopologyCase ForcedRotation = new("absent-overlay-forced-rotation",
        AbsentOverlayRotation, [(new RegionCoord(0, 0), null)], new(10.5f, 1.8f, -9.7f), new(10.5f, 0f, -10.5f),
        20f, 1, TileWaterTopologyWitness.HeightRuleSplit, 0.125f, [new(10.5f, -10.58f), new(10.42f, -10.5f)]);

    internal static readonly TileWaterTopologyCase CornerCuts = new("corner-cut-rotations",
        () => CornerCutBlock(slopedCutEdges: false), [(new RegionCoord(0, 0), null)], new(11f, 3.2f, -9.4f),
        new(11f, -0.3f, -11f), 20f, 1, TileWaterTopologyWitness.HeightRuleSplit, 0.125f, BlockProbes(0));

    internal static readonly TileWaterTopologyCase SlopedCornerCuts = new("sloped-corner-cut-rotations",
        () => CornerCutBlock(slopedCutEdges: true), [(new RegionCoord(0, 0), null)], new(11f, 3.2f, -9.4f),
        new(11f, -0.3f, -11f), 20f, 1, TileWaterTopologyWitness.HeightRuleSplit, 0.125f, BlockProbes(0));

    internal static readonly TileWaterTopologyCase FeatherSubdivisions = new("feather-overlay-subdivisions",
        FeatherBlock, [(new RegionCoord(0, 0), null)], new(11f, 3.2f, -9.4f), new(11f, -0.3f, -11f), 20f, 1,
        TileWaterTopologyWitness.OtherDiagonal, 0.125f,
        [.. BlockProbes(0), new(10.25f, -11f), new(10.75f, -11f), new(11.25f, -11f), new(11.75f, -11f),
         new(11.0625f, -11f), new(10.5f, -10.9375f)]);

    internal static readonly TileWaterTopologyCase DiagonalHalfOverlays = new("diagonal-half-overlays",
        DiagonalHalfBlock, [(new RegionCoord(0, 0), null)], new(11f, 3.2f, -9.4f), new(11f, -0.3f, -11f), 20f, 1,
        TileWaterTopologyWitness.HeightRuleSplit, 0.125f, BlockProbes(0));

    internal static readonly TileWaterTopologyCase CoarseInterior = new("coarse-interior-and-fans", CoarsePond,
        [(new RegionCoord(0, 0), TileRegionResidencyState.Decor)], new(22f, 9f, -10.5f), new(22f, -0.5f, -22f),
        40f, 1, TileWaterTopologyWitness.FullDetail, 0.5f,
        [new(22f, -22f), new(22f, -17f), new(18f, -18f), new(17.5f, -21.25f), new(21.75f, -16.5f)]);

    internal static readonly TileWaterTopologyCase FullCoarseBorder = new("full-coarse-region-border",
        FullCoarseNeighbours,
        [(new RegionCoord(1, 0), TileRegionResidencyState.Decor), (new RegionCoord(0, 0), TileRegionResidencyState.Gameplay)],
        new(67f, 9.5f, -10.5f), new(67f, -0.5f, -22f), 40f, 2, TileWaterTopologyWitness.FullDetail, 0.5f,
        [new(64f, -21f), new(64f, -22f), new(64f, -23.5f), new(65f, -21f), new(70f, -22f), new(66f, -22f),
         new(float.BitDecrement(64f), -22.25f), new(float.BitIncrement(64f), -22.25f)]);

    internal static readonly TileWaterTopologyCase DistantBlock = new("distant-full-block",
        () => DeepCornerBlock(DistantRegionOffset), [(new RegionCoord(DistantRegionOffset, 0), null)],
        new(11f + 64f * DistantRegionOffset, 3.2f, -9.4f), new(11f + 64f * DistantRegionOffset, -0.3f, -11f), 20f, 1,
        TileWaterTopologyWitness.ShiftedBinding, 0.125f, BlockProbes(DistantRegionOffset), JitterAmplitude: 0f);

    /// <summary>The distant block at the world origin. Its image is the parity reference of the reduced frame.</summary>
    internal static readonly TileWaterTopologyCase NearBlock = new("near-full-block", () => DeepCornerBlock(0),
        [(new RegionCoord(0, 0), null)], new(11f, 3.2f, -9.4f), new(11f, -0.3f, -11f), 20f, 1,
        TileWaterTopologyWitness.ShiftedBinding, 0.125f, BlockProbes(0), JitterAmplitude: 0f);

    internal static IReadOnlyList<TileWaterTopologyCase> All { get; } =
        [OtherDiagonal, ForcedRotation, CornerCuts, SlopedCornerCuts, FeatherSubdivisions, DiagonalHalfOverlays, CoarseInterior, FullCoarseBorder, NearBlock, DistantBlock];

    static TileWorldDocument Doc(string id, int firstRegionX, int lastRegionX)
    {
        var doc = new TileWorldDocument { Id = id, DisplayName = id, PlaneCount = 1 };
        for (int z = -1; z <= 1; z++)
            for (int x = firstRegionX; x <= lastRegionX; x++) doc.GetOrCreateRegion(new RegionCoord(x, z));
        return doc;
    }

    static void Fill(TileWorldDocument doc, ushort material, int x0, int z0, int x1, int z1)
    {
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++) doc.SetUnderlay(x, z, 0, material);
    }

    // The saddle mirrored onto the other ridge. SE and NW stay at zero, SW and NE drop by different amounts so
    // the corner rule joins the high NW-SE pair. The tile centre is dry ground, while HeightAt gives -0.4 m.
    static TileWorldDocument NwSeRidge()
    {
        TileWorldDocument doc = Doc("public-nw-se-ridge", -1, 1);
        Fill(doc, TileRenderTestData.Grass, 8, 8, 13, 13);
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Water);
        doc.SetCornerHeightCm(10, 10, 0, -100);
        doc.SetCornerHeightCm(11, 11, 0, -60);
        return doc;
    }

    // The original saddle heights, whose corner rule joins the high SW-NE pair. A diagonal-half shape with no
    // overlay material draws a full tile but still forces its diagonal, so rotation 1 joins the low NW-SE pair.
    static TileWorldDocument AbsentOverlayRotation()
    {
        TileWorldDocument doc = Doc("public-absent-overlay-rotation", -1, 1);
        Fill(doc, TileRenderTestData.Grass, 8, 8, 13, 13);
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Water);
        doc.SetCornerHeightCm(11, 10, 0, -100);
        doc.SetCornerHeightCm(10, 11, 0, -100);
        doc.SetOverlayShape(10, 10, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(10, 10, 0, 1);
        return doc;
    }

    // Two by two water tiles around one deep shared corner, with grass banks at zero.
    static TileWorldDocument DeepCornerBlock(int regionOffset, short deepCm = -100)
    {
        int ox = 64 * regionOffset;
        TileWorldDocument doc = Doc(regionOffset == 0 ? "public-deep-corner-block" : "public-distant-deep-corner-block",
            regionOffset - 1, regionOffset + 1);
        Fill(doc, TileRenderTestData.Grass, ox + 7, 7, ox + 15, 15);
        Fill(doc, TileRenderTestData.Water, ox + 10, 10, ox + 12, 12);
        doc.SetCornerHeightCm(ox + 11, 11, 0, deepCm);
        return doc;
    }

    // The deep corner block with a road corner cut on every tile, one per rotation (0 SW, 1 NW, 2 NE, 3 SE).
    // Flat cut edges put each cut opposite the deep corner, so both mid-edge points of every cut lie on level
    // edges. Sloped cut edges turn rotation by tile instead, so three cuts meet the deep corner and their
    // mid-edge points sit on sloping edges shared with an uncut neighbour, a long/short T junction.
    static TileWorldDocument CornerCutBlock(bool slopedCutEdges)
    {
        TileWorldDocument doc = DeepCornerBlock(0);
        int[] rotations = slopedCutEdges ? [0, 1, 2, 3] : [0, 3, 1, 2];
        int tile = 0;
        foreach ((int x, int z) in new[] { (10, 10), (11, 10), (10, 11), (11, 11) })
        {
            doc.SetOverlay(x, z, 0, TileRenderTestData.Road);
            doc.SetOverlayShape(x, z, 0, TileOverlayShape.CornerQuarter);
            doc.SetOverlayRotation(x, z, 0, rotations[tile++]);
        }
        return doc;
    }

    // A feathered full road overlay on the south pair of a block whose deep corner is an ordinary -0.70 m. The
    // mesher subdivides triangles near the overlay boundary into sixteenths, so their vertices sit on the sloping
    // shared edge at z = 11 as T junctions against the uncut north pair.
    static TileWorldDocument FeatherBlock()
    {
        TileWorldDocument doc = DeepCornerBlock(0, -70);
        foreach (int x in new[] { 10, 11 })
        {
            doc.SetOverlay(x, 10, 0, TileRenderTestData.Road);
            doc.SetSettings(x, 10, 0, doc.GetSettings(x, 10, 0) | TileSettings.FeatherOverlay);
        }
        return doc;
    }

    // Road diagonal halves with material at rotations 0 to 3. Each rotation forces its own diagonal, which differs
    // from the corner-height rule on the two southern tiles.
    static TileWorldDocument DiagonalHalfBlock()
    {
        TileWorldDocument doc = DeepCornerBlock(0, -70);
        int rotation = 0;
        foreach ((int x, int z) in new[] { (10, 10), (11, 10), (10, 11), (11, 11) })
        {
            doc.SetOverlay(x, z, 0, TileRenderTestData.Road);
            doc.SetOverlayShape(x, z, 0, TileOverlayShape.DiagonalHalf);
            doc.SetOverlayRotation(x, z, 0, rotation++);
        }
        return doc;
    }

    static Vector2[] BlockProbes(int regionOffset)
    {
        float ox = 64f * regionOffset;
        return [new(ox + 10.5f, -10.5f), new(ox + 11.5f, -10.5f), new(ox + 10.5f, -11.5f), new(ox + 11.5f, -11.5f),
            new(ox + 11f, -11f), new(ox + 10.25f, -10.25f), new(ox + 11.75f, -11.75f), new(ox + 11f, -10.5f),
            new(ox + 10.5f, -11f)];
    }

    // A twelve metre pond aligned to Coarse4 cells. The centre cell collapses to one pair, so its raised
    // interior corner at (22, 22) is not drawn. The ring cells fan from their centres to the bank.
    static TileWorldDocument CoarsePond()
    {
        TileWorldDocument doc = Doc("public-coarse-pond", -1, 1);
        Fill(doc, TileRenderTestData.Grass, 8, 8, 36, 36);
        Fill(doc, TileRenderTestData.Water, 16, 16, 28, 28);
        for (int z = 17; z <= 27; z++)
            for (int x = 17; x <= 27; x++) doc.SetCornerHeightCm(x, z, 0, -100);
        doc.SetCornerHeightCm(22, 22, 0, 0);
        return doc;
    }

    // One body across the border of a Full gameplay region and a Coarse4 decor region. The decor side has a
    // west transition fan at the border, a collapsed interior cell and undrawn raised corners.
    static TileWorldDocument FullCoarseNeighbours()
    {
        TileWorldDocument doc = Doc("public-full-coarse-border", -1, 2);
        Fill(doc, TileRenderTestData.Grass, 52, 8, 84, 36);
        Fill(doc, TileRenderTestData.Water, 60, 16, 76, 28);
        for (int z = 17; z <= 27; z++)
            for (int x = 61; x <= 75; x++) doc.SetCornerHeightCm(x, z, 0, -100);
        doc.SetCornerHeightCm(62, 22, 0, -150);
        doc.SetCornerHeightCm(65, 21, 0, 0);
        doc.SetCornerHeightCm(70, 22, 0, 0);
        return doc;
    }
}
