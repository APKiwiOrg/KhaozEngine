using System;
using System.IO;
using KhaozEngine.TileEdit;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>A throwaway directory under the OS temp root, deleted on dispose. Copied rather than shared with
/// <c>KhaozEngine.TileWorld.Tests</c>, which this project deliberately does not reference (its one reference is
/// the tool, so push CI's reference-graph test selection stays honest about what a diff touches).</summary>
public sealed class TempDir : IDisposable
{
    /// <summary>The directory itself.</summary>
    public string Path { get; }

    /// <summary>Creates the directory.</summary>
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ke-tileedit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>A path inside this directory.</summary>
    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Deletes the directory and everything in it.</summary>
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>The fixture every test here builds on: a catalog file on disk (the session loads catalogs by PATH,
/// so an in-memory <c>TileWorldCatalogs.Greybox()</c> cannot stand in) and a small world with one region, some
/// ground, a wall, a tree, a blocked tile and a marker.</summary>
public static class TileEditTestWorld
{
    /// <summary>The catalog file's name inside the world directory.</summary>
    public const string CatalogFileName = "greybox.json";

    /// <summary>A cut-down greybox catalog: three materials and four archetypes covering solid, wall and
    /// nothing, plus one non-square footprint.</summary>
    public const string CatalogJson = """
        {
          "materials": [
            { "id": 1, "name": "grass", "color": "#4d8a3a" },
            { "id": 2, "name": "dirt", "color": "#8a6a3a" },
            { "id": 3, "name": "water", "color": "#2a5a9a", "kind": "Water" }
          ],
          "archetypes": [
            { "id": "wall", "name": "wall", "meshRef": "greybox/wall.glb", "collisionKind": "Wall" },
            { "id": "tree", "name": "tree", "meshRef": "greybox/tree.glb", "collisionKind": "Solid" },
            { "id": "bush", "name": "bush", "meshRef": "greybox/bush.glb", "collisionKind": "None" },
            { "id": "bench", "name": "bench", "meshRef": "greybox/bench.glb", "sizeX": 1, "sizeZ": 2, "collisionKind": "Solid" }
          ]
        }
        """;

    /// <summary>Writes the catalog file at <paramref name="path"/>, creating its directory.</summary>
    public static void WriteCatalog(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.WriteAllText(path, CatalogJson);
    }

    /// <summary>Creates an empty world in <paramref name="directory"/> with the catalog written INSIDE it and
    /// named relatively, which is the ordinary shape a game ships, and opens it.</summary>
    public static TileEditSession NewSession(string directory, int planeCount = 4)
    {
        WriteCatalog(System.IO.Path.Combine(directory, CatalogFileName));
        var session = new TileEditSession();
        session.Create(directory, "test-world", "Test World", planeCount, 1f, new[] { CatalogFileName });
        return session;
    }

    /// <summary>The world every query test reads, built through the mutation service so the fixture itself
    /// exercises the same command path everything else does.
    ///
    /// <para>Region (0, 0) is grass on plane 0. Then: tile (0, 0) is dirt, tile (1, 0) carries overlay 2 as a
    /// rotated diagonal half, tile (2, 2) is flagged blocked, a west-facing wall stands at (1, 1), a tree at
    /// (3, 3), and a marker named <c>spawn</c> at (0, 0). The corners at (0..1, 0..1) are lifted, given NORTH
    /// FIRST like every height row in this tool, so corner (0, 1) is 200 and corner (1, 1) is 300 while the
    /// southern pair is 0 and 100.</para></summary>
    public static (long WallId, long TreeId) Build(MutationService mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        mutate.TilesFill(new TileRect(0, 0, TileRegion.Size, TileRegion.Size), 0, underlay: 1);
        mutate.TilesFill(new TileRect(0, 0, 1, 1), 0, underlay: 2);
        mutate.TilesFill(new TileRect(1, 0, 1, 1), 0, overlay: 2, shape: TileOverlayShape.DiagonalHalf, rotation: 1);
        mutate.TilesFill(new TileRect(2, 2, 1, 1), 0, settings: TileSettings.Blocked);
        mutate.HeightsSet(new TileRect(0, 0, 2, 2), 0,
            new[] { new short[] { 200, 300 }, new short[] { 0, 100 } });
        long wall = mutate.ObjectPlace("wall", 1, 1, 0, rotation: 0).ObjectId;
        long tree = mutate.ObjectPlace("tree", 3, 3, 0).ObjectId;
        mutate.MarkerSet("spawn", 0, 0, 0, new[] { "start" });
        return (wall, tree);
    }
}
