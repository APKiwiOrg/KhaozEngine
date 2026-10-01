using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Byte-exact pins on the ground mesher and the water plane rule. Each case meshes every region-plane of a
/// world and hashes the raw vertex and index memory, so any change to a position, normal, weight, slot, jitter or
/// index moves the hash. The literals were captured from the unchanged rules and are the proof that moving those
/// rules changes no geometry. A failing case is a geometry change, never a fixture to refresh.</summary>
public sealed class TileGroundGeometryGoldenTests
{
    const string Patchwork = "patchwork";
    const string Greybox = "greybox";
    const string River = "river";

    // A dangling underlay id, so the missing-slot answer lands in the bytes too.
    const ushort Dangling = 42;

    // The tile block whose overlays are marked FeatherOverlay in the feathered patchwork. It straddles the borders
    // of all four regions meeting at (64, 64), so feathered tiles meet each other and read across region edges.
    const int FeatherMin = 52;
    const int FeatherMax = 76;

    public static TheoryData<string, TileGroundLod, bool, bool, string> Cases => new()
    {
        { Patchwork, TileGroundLod.Full, true, false, "a969ac6ca2e2006e9cfe770b65013c97bb6ee05d1fd3847c7de84b27d6b476fc" },
        { Patchwork, TileGroundLod.Full, false, false, "81cb6fe392647be594e69325b200d6b053ece79f7089f3354beb0d146268f050" },
        { Patchwork, TileGroundLod.Coarse4, true, false, "ab8a46f7d6490142eab82186ea4dce48040ae3c01b8ad72239709f9fcad9059f" },
        { Patchwork, TileGroundLod.Coarse4, false, false, "0a9c5aa89d43a386da487b8a83598b1fe5a596f65e796ab4bc17db09d96593b9" },
        { Patchwork, TileGroundLod.Full, true, true, "3702e319ccca40ee539063c9169dc3d547755368fba5ae80f8a86a2c6a3b90a7" },
        { Patchwork, TileGroundLod.Full, false, true, "fd5e761b87757b9ccfeee213424fcaffd235de1247acfad645fc6b8a7c100d59" },
        { Patchwork, TileGroundLod.Coarse4, true, true, "0fab563fef02792a4fa96129235722b9a73b9265a930fcd4d2b721d4ea8da9d6" },
        { Patchwork, TileGroundLod.Coarse4, false, true, "519820b8feacf4e56e5c597cd5831ca2f9efb3c8aab59daeda318658df28e385" },
        { Greybox, TileGroundLod.Full, true, false, "a5adaef4624b93a68c3abcd74cf81bc6c001ce493f654c07ee69f2d1511f28c0" },
        { Greybox, TileGroundLod.Full, false, false, "6b99681b0f6f0f1ca1727726c672dff6a1b5a7862634c695085e4db39083acc7" },
        { Greybox, TileGroundLod.Coarse4, true, false, "0122ee3ccff4bd80aabcd896187b3b4aa35a6fc98ca0a1c4b3c43f5bef498b18" },
        { Greybox, TileGroundLod.Coarse4, false, false, "ab5d3b4fa4e54303b9f9976abbbeb15df1f48078ee8f35ad892169a1c33c4fa9" },
        { River, TileGroundLod.Full, true, false, "64f37e61f6306013fac1a56f879adf9ef16527feffbf8c5e2dcba009dc53fb7a" },
        { River, TileGroundLod.Full, false, false, "1e83e14336aabfdd254059f0c2a527f565a44324b7ae12303b5526234f1a38e5" },
        { River, TileGroundLod.Coarse4, true, false, "c3d9e9b1ad3fd6f470ffa05d3cf126c8ae3516b7aa56d62878bb465ccbecf1c3" },
        { River, TileGroundLod.Coarse4, false, false, "ea00a61dbac255716c057d263ac7651595425269c38234acc65a702846c349e4" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MesherOutputIsByteIdentical(string world, TileGroundLod lod, bool smoothNormals, bool feather,
                                            string expected)
    {
        TileWorldDocument doc = World(world, feather);
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        var options = new TileGroundMesherOptions
        {
            SmoothNormals = smoothNormals,
            Slots = TileGroundMaterials.Build(catalogs),
        };

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (RegionCoord region in SortedRegions(doc))
            for (int plane = 0; plane < doc.PlaneCount; plane++)
            {
                GltfMesh? mesh = TileGroundMesher.Build(doc, catalogs, region, plane, lod, options);
                ModelVertex[] vertices = mesh?.Vertices ?? [];
                uint[] indices = mesh?.Indices32 ?? [];
                AppendHeader(hash, region, plane, vertices.Length, indices.Length);
                hash.AppendData(MemoryMarshal.AsBytes(vertices.AsSpan()));
                hash.AppendData(MemoryMarshal.AsBytes(indices.AsSpan()));
            }

        Assert.Equal(expected, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    [Fact]
    public void WaterPlanesAreByteIdentical()
    {
        TileWorldDocument doc = TileRenderTestData.RiverWorld();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int total = 0;
        foreach (RegionCoord region in SortedRegions(doc))
            for (int plane = 0; plane < doc.PlaneCount; plane++)
            {
                IReadOnlyList<WaterPlane> planes = TileWaterPlanes.Collect(doc, catalogs, region, plane);
                total += planes.Count;
                var fields = new float[planes.Count * 5];
                for (int i = 0; i < planes.Count; i++)
                {
                    WaterPlane p = planes[i];
                    fields[i * 5] = p.CenterX;
                    fields[i * 5 + 1] = p.CenterZ;
                    fields[i * 5 + 2] = p.HalfExtentX;
                    fields[i * 5 + 3] = p.HalfExtentZ;
                    fields[i * 5 + 4] = p.SurfaceY;
                }
                AppendHeader(hash, region, plane, planes.Count, 0);
                hash.AppendData(MemoryMarshal.AsBytes(fields.AsSpan()));
            }

        // A world that collects no water would pin nothing but the headers.
        Assert.True(total > 0, "the river world collected no water planes");
        Assert.Equal("6240e6af8e334e8975bff87dc4a29b912f4579a23562a570c919317968b6b2fd",
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    static TileWorldDocument World(string name, bool feather) => name switch
    {
        Patchwork => PatchworkWorld(feather),
        Greybox => TileRenderTestData.GreyboxWorld(),
        River => TileRenderTestData.RiverWorld(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such golden world"),
    };

    // Region order is fixed by coordinate rather than left to the dictionary, so the stream is a pure function of
    // the world.
    static IEnumerable<RegionCoord> SortedRegions(TileWorldDocument doc) =>
        doc.Regions.Keys.OrderBy(r => r.Rz).ThenBy(r => r.Rx);

    // Each region-plane is framed by its coordinate and its counts, so an empty plane still lands in the stream and
    // bytes cannot slide from one region-plane into the next unnoticed.
    static void AppendHeader(IncrementalHash hash, RegionCoord region, int plane, int first, int second)
    {
        Span<int> header = [region.Rx, region.Rz, plane, first, second];
        hash.AppendData(MemoryMarshal.AsBytes(header));
    }

    // A frozen copy of the corner memo tests' patchwork: nine regions of rolling ground under a three-material
    // patchwork, with every overlay shape, NoDraw holes and a dangling id scattered through them. Copied rather
    // than shared so the golden input cannot drift with another test's fixture. Feathering marks the overlays in
    // one block after the random pass, so the random stream is the same with and without it.
    static TileWorldDocument PatchworkWorld(bool feather)
    {
        var doc = new TileWorldDocument { Id = "ground-golden", DisplayName = "Ground golden" };
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

        if (feather)
            for (int z = FeatherMin; z < FeatherMax; z++)
                for (int x = FeatherMin; x < FeatherMax; x++)
                    if (doc.GetOverlay(x, z, 0) != 0)
                        doc.SetSettings(x, z, 0, doc.GetSettings(x, z, 0) | TileSettings.FeatherOverlay);
        return doc;
    }
}
