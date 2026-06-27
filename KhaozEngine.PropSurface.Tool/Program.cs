using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Collision;
using KhaozEngine.Render3D;

// ke-propbake <manifest.json>
// For each walkable-solid prop in the kit manifest, bake a top-surface heightmap (<id>.surf) next to the glTF and
// stamp the manifest entry's "surface": true + "heightmap": "<id>.surf". Idempotent: re-running re-bakes + restamps.
if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: ke-propbake <props.manifest.json>");
    return args.Length < 1 ? 2 : 0;
}

string manifestPath = args[0];
if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine($"ke-propbake: manifest not found: {manifestPath}");
    return 2;
}

string dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
AssetManifest manifest;
try { manifest = AssetManifest.Load(manifestPath); }
catch (Exception ex) { Console.Error.WriteLine($"ke-propbake: {ex.Message}"); return 1; }

// Mutable JSON view for stamping (preserves the rest of the file).
JsonNode root;
try { root = JsonNode.Parse(File.ReadAllText(manifestPath))!; }
catch (Exception ex) { Console.Error.WriteLine($"ke-propbake: cannot parse manifest JSON: {ex.Message}"); return 1; }
JsonArray props = root["props"]!.AsArray();

int baked = 0, skipped = 0;
foreach (AssetEntry entry in manifest.Props)
{
    GltfMesh mesh;
    try { mesh = PropLoader.LoadProp(entry); }
    catch (Exception ex) { Console.Error.WriteLine($"  ! {entry.Id}: {ex.Message}"); continue; }

    if (!PropSurfaceBake.IsWalkableSolid(mesh))
    {
        Console.WriteLine($"  - {entry.Id}: thin blocker (no surface)");
        skipped++;
        continue;
    }

    string surfName = entry.Id + ".surf";
    string outPath = Path.Combine(dir, surfName);
    PropSurface surface = PropSurfaceBake.Bake(mesh);
    using (FileStream fs = File.Create(outPath)) surface.Write(fs);

    JsonObject node = props.OfType<JsonObject>().First(p => (string?)p["id"] == entry.Id);
    node["surface"] = true;
    node["heightmap"] = surfName;
    Console.WriteLine($"  + {entry.Id}: baked {surfName} ({surface.Width}x{surface.Height}, top {surface.MaxHeight:0.00} m)");
    baked++;
}

File.WriteAllText(manifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"ke-propbake: {baked} surface(s) baked, {skipped} blocker(s) skipped; manifest stamped.");
return 0;
