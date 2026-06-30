using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Collision;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;

// ke-propbake <manifest.json>
// For each prop in the kit manifest, bake a 3D collision shape (.coll). For walkable-solid props only, also bake a
// top-surface heightmap (.surf) and stamp "surface": true + "heightmap". Stamp every prop's "collisionShape".
// Idempotent: re-running re-bakes + restamps.
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

int baked = 0, blockers = 0;
foreach (AssetEntry entry in manifest.Props)
{
    GltfMesh mesh;            // normalized render mesh (for the .surf + non-proxy .coll)
    PropBakePlan plan;
    try
    {
        if (!string.IsNullOrWhiteSpace(entry.CollisionProxy))
        {
            // Authored proxy: bake the .coll from the proxy (compound of convex pieces) in the render mesh's frame.
            GltfMesh renderRaw = GltfLoader.Load(entry.File);
            mesh = PropLoader.Normalize(renderRaw, entry.HeightMeters);
            var proxyGroups = GltfLoader.LoadGroups(entry.CollisionProxy!);
            PhysicsShape proxyColl = PropCollisionBake.BakeProxy(renderRaw, entry.HeightMeters, proxyGroups);
            plan = PropBakePlan.ForProxy(mesh, proxyColl);
        }
        else
        {
            mesh = PropLoader.LoadProp(entry);
            plan = PropBakePlan.For(mesh);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"  ! {entry.Id}: {ex.Message}"); continue; }
    JsonObject node = props.OfType<JsonObject>().First(p => (string?)p["id"] == entry.Id);

    // Always bake the collision shape (.coll) and stamp collisionShape.
    string collName = entry.Id + ".coll";
    using (FileStream cfs = File.Create(Path.Combine(dir, collName))) PropCollisionBake.Write(plan.Coll, cfs);
    node["collisionShape"] = collName;
    string collKind = plan.Coll switch
    {
        CompoundShape     => "compound",
        TriangleMeshShape => "triangle-mesh",
        CylinderShape     => "cylinder",
        ConvexHullShape   => "convex-hull",
        _                 => "shape",
    };

    // Only walkable solids also get a top-surface heightmap (.surf).
    if (plan.Surface is { } surface)
    {
        string surfName = entry.Id + ".surf";
        using (FileStream fs = File.Create(Path.Combine(dir, surfName))) surface.Write(fs);
        node["surface"] = true;
        node["heightmap"] = surfName;
        Console.WriteLine($"  + {entry.Id}: baked {surfName} ({surface.Width}x{surface.Height}, top {surface.MaxHeight:0.00} m) + {collName} ({collKind})");
        baked++;
    }
    else
    {
        Console.WriteLine($"  + {entry.Id}: baked {collName} ({collKind}) [thin blocker, no surface]");
        blockers++;
    }
}

File.WriteAllText(manifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"ke-propbake: {baked} surface(s) + {baked + blockers} collision shape(s) baked, {blockers} blocker(s); manifest stamped.");
return 0;
