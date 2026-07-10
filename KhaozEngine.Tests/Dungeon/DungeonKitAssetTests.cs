using System;
using System.IO;
using KhaozEngine.Dungeon;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    /// <summary>Verifies the committed greybox dungeon-kit manifest
    /// (<c>KhaozEngine.Showcase/assets/dungeon/dungeon.manifest.json</c>) resolves every id
    /// <see cref="DungeonKitMap.Greybox"/> maps a <see cref="DungeonPiece"/> to, and that each entry's
    /// file actually exists on disk, so the committed kit and the greybox map never drift apart.
    ///
    /// No existing test in this project loads a real committed Showcase asset by path (the
    /// <c>AssetManifest</c> tests in <c>Render3D/AssetManifestTests.cs</c> and
    /// <c>Render3D/PropSurfaceLoaderTests.cs</c> only use synthetic manifests in a temp directory), so
    /// there is no established "load a Showcase asset from the test project's output" pattern to
    /// follow. This walks up from the test assembly's own directory to the repo root (identified by
    /// the checked-in <c>KhaozEngine.slnx</c>) and resolves the manifest from there, rather than
    /// depending on any MSBuild output-copy wiring.</summary>
    public class DungeonKitAssetTests
    {
        static string RepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "KhaozEngine.slnx")))
                dir = dir.Parent;
            if (dir == null)
                throw new InvalidOperationException(
                    $"DungeonKitAssetTests could not locate the repo root (KhaozEngine.slnx) above '{AppContext.BaseDirectory}'.");
            return dir.FullName;
        }

        [Fact]
        public void Manifest_ResolvesAllGreyboxKitIds()
        {
            string manifestPath = Path.Combine(RepoRoot(), "KhaozEngine.Showcase", "assets", "dungeon", "dungeon.manifest.json");
            Assert.True(File.Exists(manifestPath), $"dungeon.manifest.json not found at '{manifestPath}'.");

            AssetManifest manifest = AssetManifest.Load(manifestPath);
            DungeonKitMap map = DungeonKitMap.Greybox();

            foreach (DungeonPiece piece in Enum.GetValues<DungeonPiece>())
            {
                string id = map.Require(piece);
                AssetEntry? entry = manifest.Find(id);
                Assert.True(entry.HasValue, $"dungeon.manifest.json has no entry for '{id}' (piece {piece}).");
                Assert.True(File.Exists(entry!.Value.File),
                    $"dungeon.manifest.json entry '{id}' points at a missing file: '{entry.Value.File}'.");
            }
        }
    }
}
