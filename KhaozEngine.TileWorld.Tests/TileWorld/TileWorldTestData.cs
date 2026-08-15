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
}
