using System;
using System.IO;
using KhaozEngine.MapDoc;

namespace KhaozEngine.Tests.MapDoc;

internal sealed class NativeStorageFixture : IDisposable
{
    public MapDocument Complete { get; } = NativeResolverFixtures.Create().Document;
    public string TiledPath { get; } = Path.Combine(Path.GetTempPath(), "native-storage-" + Guid.NewGuid().ToString("N"));
    public NativeStorageFixture() => MapDocumentFile.SaveTiled(Complete, TiledPath);
    public MapDocument LoadWindow() => MapDocumentFile.LoadTiled(TiledPath, new MapTileRect(new MapTileCoord(-1, 0), new MapTileCoord(-1, 0)));
    public void Dispose() => Directory.Delete(TiledPath, recursive: true);
}
