using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.Terrain;

/// <summary>A lattice corner whose released <see cref="TileGroundMesher.CornerNormal"/> is pinned.</summary>
public readonly record struct TileFoliageReleasedCorner(int X, int Z, int Plane);

/// <summary>One invented released-sampler case: its source document, catalogs and layer, its world XZ sample
/// points and corners, and the frozen bits (height, normal X, Y, Z, density per point and normal X, Y, Z per
/// corner) the released <see cref="TileFoliageSurface"/> produced for them. A rejection case names the single
/// predicate it isolates and rejects its first point.</summary>
public sealed record TileFoliageReleasedCase(
    string Name,
    TileWorldDocument Document,
    TileWorldCatalogs Catalogs,
    TileFoliageLayer Layer,
    bool SmoothNormals,
    string? Rejects,
    IReadOnlyList<Vector2> Points,
    IReadOnlyList<uint[]> SampleBits,
    IReadOnlyList<TileFoliageReleasedCorner> Corners,
    IReadOnlyList<uint[]> CornerBits)
{
    /// <summary>A fresh released sampler over this case's source.</summary>
    public TileFoliageSurface Surface() => new(Document, Catalogs, Layer, SmoothNormals);
}

/// <summary>Invented worlds that freeze the released tile foliage sampler and corner normals. Every case is one
/// region of grass on plane 0 over an invented relief, with one feature changed from <see cref="Control"/>.
/// The pinned literals were captured from the released sampler at engine f1cd6656b, with both working-file and
/// index guards clean, before any sampler edit. Never regenerate them to bless a mismatch.</summary>
public static class TileFoliageReleasedFixtures
{
    const ushort Grass = 1;
    const ushort Dirt = 2;
    const ushort Water = 4;
    const ushort Road = 6;
    const int Tile = 30;

    static readonly TileOverlayShape[] Shapes =
    [
        TileOverlayShape.Full, TileOverlayShape.DiagonalHalf, TileOverlayShape.CornerQuarter,
        TileOverlayShape.CornerThreeQuarter,
    ];

    /// <summary>The single predicate each rejection case isolates against its control.</summary>
    public static IReadOnlyList<string> RejectionCases { get; } =
    [
        "void-underlay", "allowed-underlay", "water", "indoors", "solid", "roof", "door", "zero-raster",
        "absent-region",
    ];

    /// <summary>Released predicates the sampler ignores. Their bits equal the control's exactly.</summary>
    public static IReadOnlyList<string> IgnoredCases { get; } = ["nodraw", "blocked"];

    /// <summary>Every case name, in pinned order.</summary>
    public static IReadOnlyList<string> Names()
    {
        var names = new List<string> { "slope", "slope-faceted", "slope-2m" };
        foreach (TileOverlayShape shape in Shapes)
            for (int rotation = 0; rotation < 4; rotation++) names.Add(CutName(shape, rotation));
        names.AddRange(RejectionCases);
        names.AddRange(IgnoredCases);
        names.AddRange(["zero-raster-solid", "edge-fade", "region-edge"]);
        return names;
    }

    /// <summary>Fresh documents for every case. Documents are mutable, so each call builds new ones.</summary>
    public static IReadOnlyList<TileFoliageReleasedCase> Cases()
    {
        var cases = new List<TileFoliageReleasedCase>();
        foreach (string name in Names()) cases.Add(Case(name));
        return cases;
    }

    /// <summary>One fresh case by name.</summary>
    public static TileFoliageReleasedCase Case(string name)
    {
        Source source = Build(name);
        (uint[][] samples, uint[][] corners) = Pinned[name];
        return new TileFoliageReleasedCase(name, source.Document, TileWorldCatalogs.Greybox(), source.Layer,
            source.Smooth, source.Rejects, source.Points, samples, source.Corners, corners);
    }

    /// <summary>The case's world without its one predicate, sampled at the same points.</summary>
    public static TileFoliageReleasedCase Control(string name)
    {
        TileFoliageReleasedCase source = Case(name);
        bool allowGrassOnly = name is "allowed-underlay";
        float doorClearance = name is "door" ? 1.5f : 0f;
        TileWorldDocument document = name is "absent-region" ? World(1f, east: true) : World(1f);
        return source with
        {
            Name = name + "-control",
            Document = document,
            Layer = Layer(1f, allowed: allowGrassOnly ? [Grass] : null, doorClearance: doorClearance),
            Rejects = null,
            SampleBits = [],
            CornerBits = [],
        };
    }

    sealed record Source(TileWorldDocument Document, TileFoliageLayer Layer, bool Smooth, string? Rejects,
        Vector2[] Points, TileFoliageReleasedCorner[] Corners);

    static string CutName(TileOverlayShape shape, int rotation) =>
        "cut-" + shape.ToString().ToLowerInvariant() + "-r" + rotation.ToString(CultureInfo.InvariantCulture);

    static Source Build(string name)
    {
        Vector2[] rejection = Points(1f, (Tile + 0.5f, Tile + 0.5f), (Tile + 0.2f, Tile + 0.8f), (Tile + 3.5f, Tile + 0.5f));
        switch (name)
        {
            case "slope":
            case "slope-faceted":
            case "slope-2m":
                {
                    float size = name == "slope-2m" ? 2f : 1f;
                    TileFoliageReleasedCorner[] corners = name == "slope-faceted" ? [] : Grid(10, 12);
                    return new(World(size), Layer(size), name != "slope-faceted", null,
                        Points(size, (10.25f, 10.5f), (10.75f, 11.2f), (11.5f, 10.1f), (12.9f, 12.9f), (0.1f, 0.1f),
                            (5.5f, 7.3f)), corners);
                }
            case "void-underlay":
                return Single(name, doc => doc.SetUnderlay(Tile, Tile, 0, 0), Layer(1f), rejection);
            case "allowed-underlay":
                return Single(name, doc => doc.SetUnderlay(Tile, Tile, 0, Dirt), Layer(1f, allowed: [Grass]), rejection);
            case "water":
                return Single(name, doc => doc.SetUnderlay(Tile, Tile, 0, Water), Layer(1f), rejection);
            case "indoors":
                return Single(name, doc => doc.SetSettings(Tile, Tile, 0, TileSettings.Indoors), Layer(1f), rejection);
            case "solid":
                return Single(name, doc => doc.AddObject("tree", Tile, Tile, 0, 0), Layer(1f), rejection);
            case "roof":
                return Single(name, doc => doc.AddObject("roof_flat", Tile, Tile, 1, 0), Layer(1f), rejection,
                    [new(Tile, Tile, 1), new(Tile + 1, Tile, 1)]);
            case "door":
                return Single(name, doc => doc.AddObject("doorway", Tile, Tile, 0, 0, ["door"]),
                    Layer(1f, doorClearance: 1.5f),
                    Points(1f, (Tile + 0.5f, Tile + 0.5f), (Tile + 1.7f, Tile + 0.5f), (Tile + 2.5f, Tile + 0.5f)));
            case "zero-raster":
                return Single(name, _ => { }, Layer(1f, zeroAt: (Tile + 0.5f, Tile + 0.5f)), rejection);
            case "absent-region":
                return Single(name, _ => { }, Layer(1f),
                    Points(1f, (64.5f, 20.5f), (64.1f, 20.9f), (63.5f, 20.5f)));
            case "nodraw":
                return Single(name, doc => doc.SetSettings(Tile, Tile, 0, TileSettings.NoDraw), Layer(1f), rejection, rejects: false);
            case "blocked":
                return Single(name, doc => doc.SetSettings(Tile, Tile, 0, TileSettings.Blocked), Layer(1f), rejection, rejects: false);
            case "zero-raster-solid":
                return Single(name, doc => doc.AddObject("tree", Tile, Tile, 0, 0),
                    Layer(1f, zeroAt: (Tile + 0.5f, Tile + 0.5f)), rejection, rejects: false);
            case "edge-fade":
                return Single(name, doc =>
                    {
                        doc.SetUnderlay(Tile + 1, Tile, 0, Dirt);
                        doc.SetUnderlay(Tile, Tile + 1, 0, Water);
                    }, Layer(1f, allowed: [Grass], edgeFade: 0.5f),
                    Points(1f, (Tile + 0.9f, Tile + 0.5f), (Tile + 0.5f, Tile + 0.95f), (Tile + 0.5f, Tile + 0.5f),
                        (Tile + 0.1f, Tile + 0.1f)), rejects: false);
            case "region-edge":
                return new(World(1f), Layer(1f, edgeFade: 0.75f), true, null,
                    Points(1f, (63.9f, 20.5f), (63.5f, 20.5f), (64.5f, 20.5f), (-0.5f, 20.5f), (20.5f, 63.95f)),
                    [new(63, 20, 0), new(64, 20, 0), new(65, 20, 0), new(0, 20, 0), new(-1, 20, 0), new(20, 64, 0)]);
        }

        foreach (TileOverlayShape shape in Shapes)
            for (int rotation = 0; rotation < 4; rotation++)
            {
                if (name != CutName(shape, rotation)) continue;
                TileWorldDocument doc = World(1f);
                doc.SetOverlay(40, 40, 0, Road);
                doc.SetOverlayShape(40, 40, 0, shape);
                doc.SetOverlayRotation(40, 40, 0, rotation);
                TileFoliageReleasedCorner[] corners = shape == TileOverlayShape.Full && rotation == 0
                    ? [new(40, 40, 0), new(41, 40, 0), new(40, 41, 0), new(41, 41, 0)]
                    : [];
                return new(doc, Layer(1f, allowed: [Grass]), true, null,
                    Points(1f, (40.2f, 40.2f), (40.8f, 40.2f), (40.2f, 40.8f), (40.8f, 40.8f), (40.5f, 40.5f),
                        (40.35f, 40.6f)), corners);
            }
        throw new ArgumentOutOfRangeException(nameof(name), name, "unknown released foliage case");
    }

    static Source Single(string name, Action<TileWorldDocument> change, TileFoliageLayer layer, Vector2[] points,
        TileFoliageReleasedCorner[]? corners = null, bool rejects = true)
    {
        TileWorldDocument doc = World(1f);
        change(doc);
        return new(doc, layer, true, rejects ? name : null, points, corners ?? []);
    }

    static Vector2[] Points(float tileSize, params (float X, float Z)[] tiles)
    {
        var points = new Vector2[tiles.Length];
        for (int i = 0; i < tiles.Length; i++)
            points[i] = new Vector2(TileWorldSpace.WorldX(tiles[i].X, tileSize), TileWorldSpace.WorldZ(tiles[i].Z, tileSize));
        return points;
    }

    static TileFoliageReleasedCorner[] Grid(int lo, int hi)
    {
        var corners = new List<TileFoliageReleasedCorner>();
        for (int z = lo; z <= hi; z++)
            for (int x = lo; x <= hi; x++) corners.Add(new(x, z, 0));
        return [.. corners];
    }

    /// <summary>One region of grass on plane 0 over an invented noncoplanar relief. East adds the region east of
    /// it, flat grass, for the absent-region control.</summary>
    static TileWorldDocument World(float tileSize, bool east = false)
    {
        var doc = new TileWorldDocument { Id = "foliage-released", DisplayName = "Foliage released", TileSize = tileSize };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
            {
                doc.SetUnderlay(x, z, 0, Grass);
                doc.SetCornerHeightCm(x, z, 0, (short)((x * 37 + z * 11) % 23 * 5 - 40));
            }
        if (east)
        {
            doc.GetOrCreateRegion(new RegionCoord(1, 0));
            for (int z = 0; z < TileRegion.Size; z++)
                for (int x = 0; x < TileRegion.Size; x++) doc.SetUnderlay(TileRegion.Size + x, z, 0, Grass);
        }
        return doc;
    }

    /// <summary>The invented layer: 36 by 36 cells of twice the tile size covering the region with a margin,
    /// varied nonzero bytes, and optionally the four cells around one tile point zeroed.</summary>
    static TileFoliageLayer Layer(float tileSize, ushort[]? allowed = null, float edgeFade = 0f,
        float doorClearance = 0f, (float X, float Z)? zeroAt = null)
    {
        const int size = 36;
        float originX = -2f * tileSize;
        float originZ = -66f * tileSize;
        float cell = 2f * tileSize;
        var bytes = new byte[size * size];
        for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++) bytes[z * size + x] = (byte)(96 + (x * 29 + z * 17) % 160);
        if (zeroAt is { } tile)
        {
            int u = (int)MathF.Floor((TileWorldSpace.WorldX(tile.X, tileSize) - originX) / cell);
            int v = (int)MathF.Floor((TileWorldSpace.WorldZ(tile.Z, tileSize) - originZ) / cell);
            for (int z = v; z <= v + 1; z++)
                for (int x = u; x <= u + 1; x++) bytes[z * size + x] = 0;
        }
        return new TileFoliageLayer("cover-a", 0, originX, originZ, cell, size, size, bytes, 17, 0.5f, 0.8f, 1.4f,
            -0.01f, [new TileFoliageArchetype("reed-a", 3f), new TileFoliageArchetype("reed-b", 1f)], allowed,
            excludeIndoors: true, excludeSolidObjects: true, doorClearance: doorClearance, edgeFade: edgeFade);
    }

    public static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    // Captured once by the released Sample and CornerNormal at b12a5977c, whose sampler and mesher files equal
    // f1cd6656b in both the working tree and the index. Rows: height, normal X, Y, Z, density, then corner normals.
    static readonly Dictionary<string, (uint[][] Samples, uint[][] Corners)> Pinned = new(StringComparer.Ordinal)
    {
        ["slope"] = (
            [
                [0x3EF33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x3F0CECEDu],
                [0x3ECF5C2Du, 0xBC8FDB7Au, 0x3F7FE16Du, 0xBCCCB454u, 0x3F275423u],
                [0xBCA3D698u, 0x3E3F1F31u, 0x3F74BD4Bu, 0x3E67B633u, 0x3F0167CDu],
                [0xBE851EBAu, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27F4u, 0x3EF7C48Fu],
                [0xBEC7AE13u, 0xBE151A54u, 0x3F7CBD38u, 0x3D835182u, 0x3F4F6903u],
                [0xBC23D698u, 0x3E9C2FD5u, 0x3F71B7F5u, 0x3DFE2FB0u, 0x3F6093C8u],
            ],
            [
                [0xBDFDF1EBu, 0x3F7DF1ECu, 0xBCCB27EFu],
                [0x3EBBCEDDu, 0x3F50ACF6u, 0x3EE58B0Eu],
                [0xBDFDF1EBu, 0x3F7DF1ECu, 0xBCCB27EBu],
                [0xBDFDF1ECu, 0x3F7DF1ECu, 0xBCCB27E3u],
                [0xBDFDF1ECu, 0x3F7DF1ECu, 0xBCCB27EFu],
                [0x3ED20D58u, 0x3F69642Au, 0xBCBAB68Bu],
                [0xBDFDF1EBu, 0x3F7DF1ECu, 0xBCCB27EFu],
                [0x3ED20D58u, 0x3F69642Au, 0xBCBAB68Bu],
                [0xBDFDF1ECu, 0x3F7DF1ECu, 0xBCCB27F3u],
            ]),
        ["slope-faceted"] = (
            [
                [0x3EF33332u, 0xBF03C01Eu, 0x3F3C3707u, 0xBEE1DBA1u, 0x3F0CECEDu],
                [0x3ECF5C2Du, 0xBF03C01Eu, 0x3F3C3706u, 0xBEE1DBA1u, 0x3F275423u],
                [0xBCA3D698u, 0x3EBBCEDCu, 0x3F50ACF5u, 0x3EE58B0Eu, 0x3F0167CDu],
                [0xBE851EBAu, 0xBF03C01Eu, 0x3F3C3706u, 0xBEE1DBA1u, 0x3EF7C48Fu],
                [0xBEC7AE13u, 0xBF03C01Eu, 0x3F3C3707u, 0xBEE1DBA0u, 0x3F4F6903u],
                [0xBC23D698u, 0x3EBBCEDDu, 0x3F50ACF6u, 0x3EE58B0Eu, 0x3F6093C8u],
            ],
            [
            ]),
        ["slope-2m"] = (
            [
                [0x3EF33332u, 0xBD7F7B48u, 0x3F7F7B49u, 0xBC4C629Du, 0x3F0CECEDu],
                [0x3ECF5C2Du, 0xBBC5DD92u, 0x3F7FF9B0u, 0xBC4CC7BDu, 0x3F275423u],
                [0xBCA3D698u, 0x3DD703D2u, 0x3F7C8ADFu, 0x3E00BE6Fu, 0x3F0167CDu],
                [0xBE851EBAu, 0xBD7F7B4Au, 0x3F7F7B4Au, 0xBC4C62A4u, 0x3EF7C48Fu],
                [0xBEC7AE13u, 0xBD968C6Au, 0x3F7F2C38u, 0x3D04A9A4u, 0x3F4F6903u],
                [0xBC23D698u, 0x3E271FBBu, 0x3F7BEDA4u, 0x3D8FA9A2u, 0x3F6093C8u],
            ],
            [
                [0xBD7F7B48u, 0x3F7F7B49u, 0xBC4C62A0u],
                [0x3E591A45u, 0x3F7139A3u, 0x3E84AC7Fu],
                [0xBD7F7B48u, 0x3F7F7B49u, 0xBC4C629Cu],
                [0xBD7F7B49u, 0x3F7F7B49u, 0xBC4C6294u],
                [0xBD7F7B49u, 0x3F7F7B49u, 0xBC4C62A0u],
                [0x3E60C386u, 0x3F79BCCEu, 0xBC47CA41u],
                [0xBD7F7B48u, 0x3F7F7B49u, 0xBC4C62A0u],
                [0x3E60C386u, 0x3F79BCCEu, 0xBC47CA41u],
                [0xBD7F7B49u, 0x3F7F7B49u, 0xBC4C62A4u],
            ]),
        ["cut-full-r0"] = (
            [
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x00000000u],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x00000000u],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x00000000u],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x00000000u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x00000000u],
            ],
            [
                [0x3EBBCEDDu, 0x3F50ACF6u, 0x3EE58B0Eu],
                [0xBDFDF1EBu, 0x3F7DF1ECu, 0xBCCB27EBu],
                [0xBDFDF1ECu, 0x3F7DF1ECu, 0xBCCB27EFu],
                [0x3ED20D58u, 0x3F69642Au, 0xBCBAB68Bu],
            ]),
        ["cut-full-r1"] = (
            [
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x00000000u],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x00000000u],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x00000000u],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x00000000u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x00000000u],
            ],
            [
            ]),
        ["cut-full-r2"] = (
            [
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x00000000u],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x00000000u],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x00000000u],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x00000000u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x00000000u],
            ],
            [
            ]),
        ["cut-full-r3"] = (
            [
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x00000000u],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x00000000u],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x00000000u],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x00000000u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x00000000u],
            ],
            [
            ]),
        ["cut-diagonalhalf-r0"] = (
            [
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x3EFF65CDu],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x3F086ED4u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x00000000u],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x3F03501Du],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x3F018182u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x00000000u],
            ],
            [
            ]),
        ["cut-diagonalhalf-r1"] = (
            [
                [0x3E2E1480u, 0x3E3A006Fu, 0x3F719EE1u, 0x3E8D5331u, 0x3EFF65CDu],
                [0xBDCCCC64u, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27ECu, 0x3F086ED4u],
                [0x3EFFFFE6u, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27EFu, 0x3EF5285Du],
                [0x3E6B851Au, 0x3E50B73Eu, 0x3F7A8C33u, 0xBCC87028u, 0x00000000u],
                [0x3E4CCCCDu, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x3F018182u],
                [0x3EA51EB3u, 0xBDCE203Du, 0x3F7EB32Du, 0xBA976C60u, 0x3EFCF022u],
            ],
            [
            ]),
        ["cut-diagonalhalf-r2"] = (
            [
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x00000000u],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x3EF5285Du],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x00000000u],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x00000000u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x3EFCF022u],
            ],
            [
            ]),
        ["cut-diagonalhalf-r3"] = (
            [
                [0x3E2E1480u, 0x3E3A006Fu, 0x3F719EE1u, 0x3E8D5331u, 0x00000000u],
                [0xBDCCCC64u, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27ECu, 0x00000000u],
                [0x3EFFFFE6u, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27EFu, 0x00000000u],
                [0x3E6B851Au, 0x3E50B73Eu, 0x3F7A8C33u, 0xBCC87028u, 0x3F03501Du],
                [0x3E4CCCCDu, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0x3EA51EB3u, 0xBDCE203Du, 0x3F7EB32Du, 0xBA976C60u, 0x00000000u],
            ],
            [
            ]),
        ["cut-cornerquarter-r0"] = (
            [
                [0x3E2E1480u, 0x3E374642u, 0x3F71F09Au, 0x3E8C0673u, 0x00000000u],
                [0xBDCCCC64u, 0x3DB1B761u, 0x3F7E3F3Cu, 0x3DA03199u, 0x3F086ED4u],
                [0x3EFFFFE7u, 0xBCA5E352u, 0x3F7FDC22u, 0x3CD648A2u, 0x3EF5285Du],
                [0x3E6B851Au, 0x3E9D6663u, 0x3F73806Du, 0x3CE093D5u, 0x3F03501Du],
                [0x3E4CCCCDu, 0x3E0F7E6Bu, 0x3F7C128Cu, 0x3DD4F3DEu, 0x3F018182u],
                [0x3EA51EB3u, 0x3D725819u, 0x3F7EC56Bu, 0x3D9FA5EAu, 0x3EFCF022u],
            ],
            [
            ]),
        ["cut-cornerquarter-r1"] = (
            [
                [0x3E2E1480u, 0x3E923005u, 0x3F675663u, 0x3EA36ABEu, 0x3EFF65CDu],
                [0xBDCCCC66u, 0xBCAB6EBFu, 0x3F7FE01Cu, 0x3CBD7AC8u, 0x3F086ED4u],
                [0x3EFFFFE6u, 0x3DB3A537u, 0x3F7E41B3u, 0x3D9D08ACu, 0x00000000u],
                [0x3E6B851Au, 0x3E4FDCE1u, 0x3F7A9789u, 0xBCC8793Bu, 0x3F03501Du],
                [0x3E4CCCCEu, 0x3E100068u, 0x3F7C3A03u, 0x3DC7791Au, 0x3F018182u],
                [0x3EA51EB3u, 0x3E60F613u, 0x3F76EB1Eu, 0x3E15E57Bu, 0x3EFCF022u],
            ],
            [
            ]),
        ["cut-cornerquarter-r2"] = (
            [
                [0x3E2E1480u, 0x3E923005u, 0x3F675663u, 0x3EA36ABEu, 0x3EFF65CDu],
                [0xBDCCCC66u, 0xBCAB6EBFu, 0x3F7FE01Cu, 0x3CBD7AC8u, 0x3F086ED4u],
                [0x3EFFFFE7u, 0x3DB14ABBu, 0x3F7E5C12u, 0x3D94D10Au, 0x3EF5285Du],
                [0x3E6B851Au, 0x3E4F0901u, 0x3F7AA27Eu, 0xBCC881FFu, 0x00000000u],
                [0x3E4CCCCEu, 0x3E100068u, 0x3F7C3A03u, 0x3DC7791Au, 0x3F018182u],
                [0x3EA51EB3u, 0x3E60F613u, 0x3F76EB1Eu, 0x3E15E57Bu, 0x3EFCF022u],
            ],
            [
            ]),
        ["cut-cornerquarter-r3"] = (
            [
                [0x3E2E1480u, 0x3E389A2Eu, 0x3F71C8F1u, 0x3E8CA87Bu, 0x3EFF65CDu],
                [0xBDCCCC65u, 0x3DB3A537u, 0x3F7E41B3u, 0x3D9D08ACu, 0x00000000u],
                [0x3EFFFFE7u, 0xBCA5E352u, 0x3F7FDC22u, 0x3CD648A2u, 0x3EF5285Du],
                [0x3E6B851Au, 0x3E9D6663u, 0x3F73806Du, 0x3CE093D5u, 0x3F03501Du],
                [0x3E4CCCCDu, 0x3E0F7E6Bu, 0x3F7C128Cu, 0x3DD4F3DEu, 0x3F018182u],
                [0x3EA51EB3u, 0x3D725819u, 0x3F7EC56Bu, 0x3D9FA5EAu, 0x3EFCF022u],
            ],
            [
            ]),
        ["cut-cornerthreequarter-r0"] = (
            [
                [0x3E2E1480u, 0x3E374642u, 0x3F71F09Au, 0x3E8C0673u, 0x3EFF65CDu],
                [0xBDCCCC64u, 0x3DB1B761u, 0x3F7E3F3Cu, 0x3DA03199u, 0x00000000u],
                [0x3EFFFFE7u, 0xBCA5E352u, 0x3F7FDC22u, 0x3CD648A2u, 0x00000000u],
                [0x3E6B851Au, 0x3E9D6663u, 0x3F73806Du, 0x3CE093D5u, 0x00000000u],
                [0x3E4CCCCDu, 0x3E0F7E6Bu, 0x3F7C128Cu, 0x3DD4F3DEu, 0x00000000u],
                [0x3EA51EB3u, 0x3D725819u, 0x3F7EC56Bu, 0x3D9FA5EAu, 0x00000000u],
            ],
            [
            ]),
        ["cut-cornerthreequarter-r1"] = (
            [
                [0x3E2E1480u, 0x3E923005u, 0x3F675663u, 0x3EA36ABEu, 0x00000000u],
                [0xBDCCCC66u, 0xBCAB6EBFu, 0x3F7FE01Cu, 0x3CBD7AC8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DB3A537u, 0x3F7E41B3u, 0x3D9D08ACu, 0x3EF5285Du],
                [0x3E6B851Au, 0x3E4FDCE1u, 0x3F7A9789u, 0xBCC8793Bu, 0x00000000u],
                [0x3E4CCCCEu, 0x3E100068u, 0x3F7C3A03u, 0x3DC7791Au, 0x00000000u],
                [0x3EA51EB3u, 0x3E60F613u, 0x3F76EB1Eu, 0x3E15E57Bu, 0x00000000u],
            ],
            [
            ]),
        ["cut-cornerthreequarter-r2"] = (
            [
                [0x3E2E1480u, 0x3E923005u, 0x3F675663u, 0x3EA36ABEu, 0x00000000u],
                [0xBDCCCC66u, 0xBCAB6EBFu, 0x3F7FE01Cu, 0x3CBD7AC8u, 0x00000000u],
                [0x3EFFFFE7u, 0x3DB14ABBu, 0x3F7E5C12u, 0x3D94D10Au, 0x00000000u],
                [0x3E6B851Au, 0x3E4F0901u, 0x3F7AA27Eu, 0xBCC881FFu, 0x3F03501Du],
                [0x3E4CCCCEu, 0x3E100068u, 0x3F7C3A03u, 0x3DC7791Au, 0x00000000u],
                [0x3EA51EB3u, 0x3E60F613u, 0x3F76EB1Eu, 0x3E15E57Bu, 0x00000000u],
            ],
            [
            ]),
        ["cut-cornerthreequarter-r3"] = (
            [
                [0x3E2E1480u, 0x3E389A2Eu, 0x3F71C8F1u, 0x3E8CA87Bu, 0x00000000u],
                [0xBDCCCC65u, 0x3DB3A537u, 0x3F7E41B3u, 0x3D9D08ACu, 0x3F086ED4u],
                [0x3EFFFFE7u, 0xBCA5E352u, 0x3F7FDC22u, 0x3CD648A2u, 0x00000000u],
                [0x3E6B851Au, 0x3E9D6663u, 0x3F73806Du, 0x3CE093D5u, 0x00000000u],
                [0x3E4CCCCDu, 0x3E0F7E6Bu, 0x3F7C128Cu, 0x3DD4F3DEu, 0x00000000u],
                [0x3EA51EB3u, 0x3D725819u, 0x3F7EC56Bu, 0x3D9FA5EAu, 0x00000000u],
            ],
            [
            ]),
        ["void-underlay"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u],
                [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u],
            ],
            [
            ]),
        ["allowed-underlay"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u],
                [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u],
            ],
            [
            ]),
        ["water"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u],
                [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u],
            ],
            [
            ]),
        ["indoors"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u],
                [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u],
            ],
            [
            ]),
        ["solid"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u],
                [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u],
            ],
            [
            ]),
        ["roof"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u],
                [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u],
            ],
            [
                [0xBDFDF1ECu, 0x3F7DF1ECu, 0xBCCB27E3u],
                [0xBDFDF1ECu, 0x3F7DF1ECu, 0xBCCB27E3u],
            ]),
        ["door"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0x3D23D79Cu, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27F3u, 0x00000000u],
                [0x3F19999Au, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27F0u, 0x3F5ADADBu],
            ],
            [
            ]),
        ["zero-raster"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u],
                [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3EB45454u],
            ],
            [
            ]),
        ["absent-region"] = (
            [
                [0x3CCCCCCCu, 0x3E2CDC95u, 0x3F7C4E2Au, 0xBC4ED8DBu, 0x00000000u],
                [0x3D385333u, 0x3D5B8860u, 0x3F7F9115u, 0xBCB8E71Du, 0x00000000u],
                [0x3EB33333u, 0xBE1EE3C4u, 0x3F7CD209u, 0xBCCA41A3u, 0x3F191919u],
            ],
            [
            ]),
        ["nodraw"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x3F65E5E6u],
                [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x3F5EF894u],
                [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u],
            ],
            [
            ]),
        ["blocked"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x3F65E5E6u],
                [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x3F5EF894u],
                [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u],
            ],
            [
            ]),
        ["zero-raster-solid"] = (
            [
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u],
                [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3EB45454u],
            ],
            [
            ]),
        ["edge-fade"] = (
            [
                [0x3E2E1486u, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27F1u, 0x3E3C93CEu],
                [0x3DA3D6CEu, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x3DB4D7FAu],
                [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x3F65E5E6u],
                [0x3E9EB852u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27ECu, 0x3F637D16u],
            ],
            [
            ]),
        ["region-edge"] = (
            [
                [0x3EB33333u, 0x3DD75E49u, 0x3F7E8044u, 0xBCCB99D1u, 0x3DA98350u],
                [0x3EB33333u, 0xBE1EE3C4u, 0x3F7CD209u, 0xBCCA41A3u, 0x3ECC2177u],
                [0x3CCCCCCCu, 0x3E2CDC95u, 0x3F7C4E2Au, 0xBC4ED8DBu, 0x00000000u],
                [0x3E000000u, 0x3E0B4B3Bu, 0x3F7D99B5u, 0xBC4BDFD3u, 0x00000000u],
                [0x3E999999u, 0xBDFACD1Au, 0x3F7ACD1Au, 0xBE229373u, 0x3D0026E8u],
            ],
            [
                [0xBEA917A4u, 0x3F718F7Cu, 0xBCC13F96u],
                [0x3E9E3514u, 0x3F73655Bu, 0xBCC2B77Bu],
                [0x3E9E4087u, 0x3F7376F7u, 0x00000000u],
                [0x3DCBB878u, 0x3F7EA697u, 0xBCCBB87Cu],
                [0xBDFE05ECu, 0x3F7E05ECu, 0x00000000u],
                [0xBDFDF1ECu, 0x3F7DF1ECu, 0x3CCB27EFu],
            ]),
    };
}
