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
    /// The manifest is read from the test output directory (the project idiom for committed assets,
    /// see <c>Render3D/GltfLoaderTangentTests.cs</c>): the Showcase csproj's <c>assets/dungeon/**</c>
    /// copy item flows transitively into this project's output via the Showcase project
    /// reference.</summary>
    public class DungeonKitAssetTests
    {
        [Fact]
        public void Manifest_ResolvesAllGreyboxKitIds()
        {
            string manifestPath = Path.Combine(AppContext.BaseDirectory, "assets", "dungeon", "dungeon.manifest.json");
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
