using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        public AssetEntry(string id, string file, float heightMeters, string source, string license)
        {
            Id = id; File = file; HeightMeters = heightMeters; Source = source; License = license;
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
                entries.Add(new AssetEntry(p.Id!, ResolveFile(p.File!, baseDir), p.HeightMeters,
                                           p.Source ?? "", p.License ?? ""));
            }
            return new AssetManifest(entries);
        }

        static string ResolveFile(string file, string? baseDir)
            => string.IsNullOrEmpty(baseDir) || Path.IsPathRooted(file) ? file : Path.Combine(baseDir, file);

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
            }
        }
    }
}
