using System;
using System.IO;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Water;

namespace KhaozEngine.Tests.MapEditTool;

internal static class WaterToolFixtures
{
    internal static MapEditSession Session(MapDocument document)
    {
        string directory = WaterFixtures.TempDirectory();
        try
        {
            MapAssetClosure assets = WaterFixtures.Assets();
            foreach (MapAssetRef root in assets.Roots) Write(assets.GetResource(root.Id));
            string path = Path.Combine(directory, "map.json");
            MapDocumentFile.Save(document, path);
            var session = new MapEditSession();
            session.Open(path);
            if (document.ResolverIdentity is not null) session.BindNativeAssets(assets);
            return session;

            void Write(MapResolvedResource resource)
            {
                string resourcePath = Path.GetFullPath(resource.Reference.Path, directory);
                Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
                File.WriteAllBytes(resourcePath, resource.Bytes.ToArray());
                if (resource.Kind != MapResourceKind.Manifest) return;
                JsonNode manifest = JsonNode.Parse(resource.Bytes.Span)!;
                foreach (JsonNode? child in manifest["resources"]!.AsArray())
                    Write(assets.GetResource(child!["reference"]!["id"]!.GetValue<string>()));
            }
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    internal static MapDocument Document(MapEditSession session) => session.WithDocument((document, _) => document);

    internal static MapDocument LegacyAnalytic() => MapDocumentFileTests.SampleDoc();

    internal static void Cleanup(MapEditSession session) => Directory.Delete(Path.GetDirectoryName(session.DocumentPath!)!, recursive: true);
}
