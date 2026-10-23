using System;
using System.IO;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class TiledSnapshotValidationTests
{
    [Fact]
    public void CompleteTiledValidation_DoesNotUseEmbeddingEnvelope()
    {
        var document = PrefabTiledSnapshotFixtures.Create(257).Document;
        document.Prefabs = null;
        document.NativeAssets.Clear();
        string root = Path.Combine(Path.GetTempPath(), "khaozengine-task15-tool-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(document, root);
            var session = new MapEditSession(); session.Open(root);
            var result = session.Validate(verifyWholeWorld: true);
            Assert.True(result.Valid, string.Join("\n", result.SchemaErrors));
            Assert.True(result.WholeWorldValid);
            Assert.Equal("document", result.SchemaScope);
            var loaded = MapDocumentFile.LoadTiled(root);
            var snapshot = NativeDocumentSnapshot.Clone(loaded, MapDocRegistry.CreateDefault());
            Assert.Equal(257, snapshot.Surfaces.Patches.Count);
            snapshot.Bounds.MaxX = 100;
            snapshot.Surfaces.Patches.Clear();
            Assert.Equal(32, loaded.Bounds.MaxX);
            Assert.Equal(257, loaded.Surfaces.Patches.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
