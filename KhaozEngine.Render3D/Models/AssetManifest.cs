using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Collision;

namespace KhaozEngine.Render3D
{
    /// <summary>One prop-kit asset declaration from a manifest: its stable <see cref="Id"/>, the (relative or
    /// absolute) glTF <see cref="File"/> path, the real-world <see cref="HeightMeters"/> the loader normalizes it
    /// to, and the CC0/CC-BY <see cref="Source"/>/<see cref="License"/> provenance. Decompressed glTF only - the
    /// engine has no meshopt decoder (see <see cref="PropLoader"/>).</summary>
    public readonly struct AssetEntry
    {
        public string Id { get; }
        /// <summary>Path to the decompressed glTF/GLB. Resolved against the manifest directory when the raw value
        /// was relative (see <see cref="AssetManifest.Load"/> / <see cref="AssetManifest.Parse"/>).</summary>
        public string File { get; }
        /// <summary>The asset's intended real-world height in metres; <see cref="PropLoader.LoadProp"/> scales the
        /// mesh uniformly to this and validates it against the human-scale plausibility band.</summary>
        public float HeightMeters { get; }
        public string Source { get; }
        public string License { get; }
        /// <summary>Optional static-collision footprint for this prop (a cylinder radius or box half-extents, at
        /// unit scale). Null when the manifest declares none. Placed per scatter instance by
        /// <c>KhaozEngine.Terrain.PropColliders.FromScatter</c>.</summary>
        public ColliderShape? Collider { get; }
        /// <summary>True when this prop is a walkable solid (a rock/log/building you can stand on), so the
        /// <c>ke-propbake</c> tool bakes a top-surface heightmap for it; false (default) for a thin blocker (a
        /// tree). Set by the bake tool's classification, overridable in the manifest.</summary>
        public bool Surface { get; }
        /// <summary>Path to the baked top-surface heightmap (<c>.surf</c>) for this prop, or null when none.
        /// Resolved against the manifest directory like <see cref="File"/>. Read render-free by
        /// <c>PropSurfaceLoader</c> and the headless server.</summary>
        public string? Heightmap { get; }
        /// <summary>Path to the baked 3D collision shape (<c>.coll</c>) for this prop, or null when none.
        /// Resolved against the manifest directory like <see cref="File"/>. Read by
        /// <c>PropCollisionLoader</c> into a <c>KhaozEngine.Physics.PhysicsShape</c>.</summary>
        public string? CollisionShape { get; }
        /// <summary>Path to an authored simplified collision PROXY glTF (<c>&lt;id&gt;_collision.glb</c>) for this
        /// prop, or null when none. When set, <c>ke-propbake</c> bakes the <c>.coll</c> from the proxy (a compound
        /// of convex pieces) instead of the full render mesh. Resolved against the manifest directory like
        /// <see cref="File"/>.</summary>
        public string? CollisionProxy { get; }
        /// <summary>True when this prop's glTF ships baseColor/normal/roughness textures that should be read and
        /// bound (via <see cref="PropLoader.LoadPropWithMaterial"/>). Default false: the prop renders with its flat
        /// per-material base colour as before. Degrades gracefully if a flagged asset turns out to have no textures.</summary>
        public bool Textured { get; }
        /// <summary>Optional grouping label (e.g. <c>"trees"</c>, <c>"rocks"</c>, <c>"buildings"</c>) for kind
        /// palettes and other kit browsers. Null when the manifest declares none, in which case a consumer such as
        /// <c>KhaozEngine.MapEditor.ViewportWorld.KindCategories</c> falls back to the manifest's own file stem.</summary>
        public string? Category { get; }
        public AssetEntry(string id, string file, float heightMeters, string source, string license,
                          ColliderShape? collider = null, bool surface = false, string? heightmap = null,
                          string? collisionShape = null, string? collisionProxy = null, bool textured = false,
                          string? category = null)
        {
            Id = id; File = file; HeightMeters = heightMeters; Source = source; License = license; Collider = collider;
            Surface = surface; Heightmap = heightmap; CollisionShape = collisionShape; CollisionProxy = collisionProxy;
            Textured = textured; Category = category;
        }
    }

    /// <summary>Parses a prop-kit asset manifest: a JSON object <c>{ "props": [ { id, file, heightMeters, source,
    /// license } ] }</c>. The manifest drives both scale (<see cref="AssetEntry.HeightMeters"/>) and provenance
    /// (the CC0 attribution record). Relative <c>file</c> paths resolve against the manifest's own directory so a
    /// committed kit is self-contained. Malformed JSON / a missing <c>props</c> array / an entry missing
    /// <c>id</c> or <c>file</c> throws <see cref="InvalidOperationException"/> with context.</summary>
    public sealed class AssetManifest
    {
        /// <summary>The parsed entries in manifest order.</summary>
        public IReadOnlyList<AssetEntry> Props { get; }

        AssetManifest(IReadOnlyList<AssetEntry> props) { Props = props; }

        /// <summary>The entry with this id, or <c>null</c> if none. Linear scan (a kit is a handful of props).</summary>
        public AssetEntry? Find(string id)
        {
            foreach (AssetEntry e in Props)
                if (e.Id == id) return e;
            return null;
        }

        /// <summary>Read + parse a manifest file, resolving each relative <c>file</c> against the manifest's
        /// directory. Throws <see cref="InvalidOperationException"/> if the file cannot be read or the JSON is
        /// malformed.</summary>
        public static AssetManifest Load(string path)
        {
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new InvalidOperationException($"AssetManifest could not read '{path}': {ex.Message}", ex);
            }
            return Parse(json, Path.GetDirectoryName(path));
        }

        /// <summary>Parse a manifest from a JSON string. When <paramref name="baseDir"/> is non-empty, each relative
        /// <c>file</c> is resolved to <c>Path.Combine(baseDir, file)</c>; an already-rooted <c>file</c> is left
        /// as-is.</summary>
        public static AssetManifest Parse(string json, string? baseDir = null)
        {
            Dto? dto;
            try { dto = JsonSerializer.Deserialize<Dto>(json, Options); }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"AssetManifest JSON is malformed: {ex.Message}", ex);
            }
            if (dto?.Props == null)
                throw new InvalidOperationException("AssetManifest JSON has no 'props' array.");

            var entries = new List<AssetEntry>(dto.Props.Count);
            foreach (Dto.Entry p in dto.Props)
            {
                if (string.IsNullOrWhiteSpace(p.Id))
                    throw new InvalidOperationException("AssetManifest entry missing 'id'.");
                if (string.IsNullOrWhiteSpace(p.File))
                    throw new InvalidOperationException($"AssetManifest entry '{p.Id}' missing 'file'.");
                string? heightmap = string.IsNullOrWhiteSpace(p.Heightmap) ? null : ResolveFile(p.Heightmap!, baseDir);
                string? collisionShape = string.IsNullOrWhiteSpace(p.CollisionShape) ? null : ResolveFile(p.CollisionShape!, baseDir);
                string? collisionProxy = string.IsNullOrWhiteSpace(p.CollisionProxy) ? null : ResolveFile(p.CollisionProxy!, baseDir);
                string? category = string.IsNullOrWhiteSpace(p.Category) ? null : p.Category;
                entries.Add(new AssetEntry(p.Id!, ResolveFile(p.File!, baseDir), p.HeightMeters,
                                           p.Source ?? "", p.License ?? "", ParseCollider(p.Id!, p.Collider),
                                           p.Surface, heightmap, collisionShape, collisionProxy, p.Textured, category));
            }
            return new AssetManifest(entries);
        }

        static string ResolveFile(string file, string? baseDir)
            => string.IsNullOrEmpty(baseDir) || Path.IsPathRooted(file) ? file : Path.Combine(baseDir, file);

        // Optional per-prop collision footprint: { "type": "cylinder", "radius" } or { "type": "box", "halfW", "halfD" }.
        static ColliderShape? ParseCollider(string id, Dto.ColliderDto? c)
        {
            if (c == null) return null;
            switch ((c.Type ?? "").Trim().ToLowerInvariant())
            {
                case "cylinder": return ColliderShape.Cylinder(c.Radius);
                case "box": return ColliderShape.Box(c.HalfW, c.HalfD);
                default:
                    throw new InvalidOperationException(
                        $"AssetManifest entry '{id}' has unknown collider type '{c.Type}' (expected 'cylinder' or 'box').");
            }
        }

        // Intentionally NOT Serialization.JsonDefaults: manifests are authored by hand / external kit
        // tooling, so tolerate any property casing rather than the engine's stricter save/wire defaults.
        static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        sealed class Dto
        {
            [JsonPropertyName("props")] public List<Entry>? Props { get; set; }

            public sealed class Entry
            {
                [JsonPropertyName("id")] public string? Id { get; set; }
                [JsonPropertyName("file")] public string? File { get; set; }
                [JsonPropertyName("heightMeters")] public float HeightMeters { get; set; }
                [JsonPropertyName("source")] public string? Source { get; set; }
                [JsonPropertyName("license")] public string? License { get; set; }
                [JsonPropertyName("collider")] public ColliderDto? Collider { get; set; }
                [JsonPropertyName("surface")] public bool Surface { get; set; }
                [JsonPropertyName("heightmap")] public string? Heightmap { get; set; }
                [JsonPropertyName("collisionShape")] public string? CollisionShape { get; set; }
                [JsonPropertyName("collisionProxy")] public string? CollisionProxy { get; set; }
                [JsonPropertyName("textured")] public bool Textured { get; set; }
                [JsonPropertyName("category")] public string? Category { get; set; }
            }

            public sealed class ColliderDto
            {
                [JsonPropertyName("type")] public string? Type { get; set; }
                [JsonPropertyName("radius")] public float Radius { get; set; }
                [JsonPropertyName("halfW")] public float HalfW { get; set; }
                [JsonPropertyName("halfD")] public float HalfD { get; set; }
            }
        }
    }
}
