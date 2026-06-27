using System.IO;
using KhaozEngine.Collision;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class PropSurfaceLoaderTests
{
    [Fact]
    public void LoadAll_ReadsReferencedHeightmaps()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke_surf_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Write a tiny .surf and a manifest that references it.
            float n = float.NaN;
            var surf = new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, 2f, n, 2f, 2f, 2f, n, 2f, n });
            using (var fs = File.Create(Path.Combine(dir, "rock.surf"))) surf.Write(fs);
            File.WriteAllText(Path.Combine(dir, "props.manifest.json"),
                """{ "props": [ { "id": "rock", "file": "rock.glb", "heightMeters": 1.8, "surface": true, "heightmap": "rock.surf" } ] }""");

            AssetManifest m = AssetManifest.Load(Path.Combine(dir, "props.manifest.json"));
            var loaded = PropSurfaceLoader.LoadAll(m);
            Assert.True(loaded.ContainsKey("rock"));
            Assert.Equal(2f, loaded["rock"].MaxHeight, 3);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadAll_SkipsEntriesWithoutHeightmap()
    {
        AssetManifest m = AssetManifest.Parse("""{ "props": [ { "id": "tree", "file": "tree.glb", "heightMeters": 10 } ] }""");
        Assert.Empty(PropSurfaceLoader.LoadAll(m));
    }
}
