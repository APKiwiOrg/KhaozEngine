using System;
using System.IO;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc;

namespace KhaozEngine.Tests.MapEditTool;

internal sealed class TerrainTransactionFixture : IDisposable
{
    readonly string _directory;
    readonly MapEditSession _session = new();

    internal EditorDocument Editor { get; }
    internal MutationService Service { get; }

    TerrainTransactionFixture(MapDocument doc, string directory)
    {
        _directory = directory;
        var assets = NativeAssetFixtures.Valid();
        foreach (MapAssetRef root in assets.Roots)
        {
            Write(root, assets.Source);
            JsonNode manifest = JsonNode.Parse(assets.Source.Read(root).Span)!;
            foreach (JsonNode? resource in manifest["resources"]!.AsArray())
            {
                JsonNode reference = resource!["reference"]!;
                Write(new(reference["id"]!.GetValue<string>(), reference["path"]!.GetValue<string>(),
                    reference["sha256"]!.GetValue<string>(), reference["payloadVersion"]!.GetValue<int>()), assets.Source);
            }
        }
        string path = Path.Combine(_directory, "map.json");
        MapDocumentFile.Save(doc, path);
        Editor = new EditorDocument(MapDocumentFile.Load(path));
        Editor.BindNativeAssets(MapAssetClosure.Load(Editor.Doc.NativeAssets, new MapDirectoryAssetSource(_directory)));
        _session.Open(path);
        Service = new MutationService(_session);
    }

    internal static TerrainTransactionFixture Create(MapDocument doc)
    {
        string directory = Path.Combine(Path.GetTempPath(), "terrain-transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { return new TerrainTransactionFixture(doc, directory); }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    void Write(MapAssetRef reference, IMapAssetSource source)
    {
        string path = Path.GetFullPath(reference.Path, _directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, source.Read(reference).ToArray());
    }

    internal string Text() => _session.IsDirty
        ? _session.WithDocument((doc, registry) => MapDocumentFile.SaveText(doc, registry))
        : MapDocumentFile.SaveText(Editor.Doc, Editor.Registry);

    internal string Token() => _session.IsDirty
        ? _session.WithDocument((doc, _) => SurfaceStorageFixtures.SemanticSnapshot(doc))
        : SurfaceStorageFixtures.SemanticSnapshot(Editor.Doc);

    internal string State() => string.Join("\n", Text(), Editor.History.UndoLabel, Editor.History.RedoLabel,
        Editor.IsDirty.ToString(), Token(), Editor.LastNativeEffects?.Describe());

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
