using System;
using System.IO;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>A throwaway directory under the OS temp root, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ke-tileworld-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class TileWorldTestData
{
    /// <summary>A world with the given regions created and every tile of plane 0 set to underlay 1 (grass),
    /// heights flat 0. Objects and markers empty.</summary>
    public static TileWorldDocument FlatWorld(int planeCount = 4, params RegionCoord[] regions)
    {
        var doc = new TileWorldDocument { Id = "test", DisplayName = "Test", PlaneCount = planeCount };
        if (regions.Length == 0) regions = new[] { new RegionCoord(0, 0) };
        foreach (RegionCoord c in regions)
        {
            TileRegion r = doc.GetOrCreateRegion(c);
            ushort[] u = r.Plane(0).UnderlayOrAlloc();
            for (int i = 0; i < u.Length; i++) u[i] = 1;
        }
        return doc;
    }

    /// <summary>The greybox catalogs plus the two shapes the editing tests need and greybox does not carry: a
    /// 2x3 solid <c>hall</c>, whose footprint is neither square nor one tile wide, so a rotation moves tiles a
    /// square archetype's would not, and a 3x1 solid <c>beam</c> for the second non-square case.</summary>
    public static TileWorldCatalogs EditingCatalogs() => TileWorldCatalogs.Merge(
        TileWorldCatalogs.Greybox(),
        TileWorldCatalogs.LoadJson(
            """
            {
              "archetypes": [
                { "id": "hall", "name": "hall", "meshRef": "test/hall.glb", "sizeX": 2, "sizeZ": 3, "collisionKind": "Solid" },
                { "id": "beam", "name": "beam", "meshRef": "test/beam.glb", "sizeX": 3, "sizeZ": 1, "collisionKind": "Solid" }
              ]
            }
            """,
            "editing-tests"));
}
