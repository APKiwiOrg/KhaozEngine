using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PixelLabSheetAssembler;

/// <summary>
/// IO boundary: resolves a PixelLab character export (zip or unzipped dir), parses metadata.json,
/// and loads the chosen animation's frames. Returns the parsed animation plus, when a zip was
/// extracted, the temp directory the caller must delete. A load that throws deletes its own
/// extraction first, since the caller never receives a path it can clean up.
/// </summary>
public static class PixelLabExport
{
    public static (CharacterAnimation Anim, string? TempDir) Load(string input, string animName)
    {
        string root;
        string? temp = null;

        if (Directory.Exists(input))
        {
            root = input;
        }
        else if (File.Exists(input) && input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            temp = Directory.CreateTempSubdirectory("pixellab_").FullName;
            ZipFile.ExtractToDirectory(input, temp);
            root = temp;
        }
        else
        {
            throw new AssemblyException($"input not found or not a .zip / directory: {input}");
        }

        try
        {
            return (LoadFrom(root, input, animName), temp);
        }
        catch
        {
            // An extracted directory only reaches the caller through the return above, so a throw from here leaves
            // the caller's cleanup with nothing to delete. Every failed run would otherwise orphan another
            // pixellab_* directory, the unknown --anim case included, whose own message invites another attempt.
            if (temp is not null)
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* best effort, same as Program.cs */ }
            }
            throw;
        }
    }

    private static CharacterAnimation LoadFrom(string root, string input, string animName)
    {
        string metaPath = Path.Combine(root, "metadata.json");
        if (!File.Exists(metaPath))
        {
            string[] found = Directory.GetFiles(root, "metadata.json", SearchOption.AllDirectories);
            if (found.Length == 0)
                throw new AssemblyException($"metadata.json not found under: {input}");
            metaPath = found[0];
            root = Path.GetDirectoryName(metaPath)!;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        JsonElement state = doc.RootElement.GetProperty("states")[0];
        string charName = state.GetProperty("character").GetProperty("name").GetString() ?? "character";

        JsonElement anims = state.GetProperty("frames").GetProperty("animations");
        if (!anims.TryGetProperty(animName, out JsonElement animEl))
        {
            IEnumerable<string> names = anims.EnumerateObject().Select(p => p.Name);
            throw new AssemblyException($"animation '{animName}' not found. Available: {string.Join(", ", names)}");
        }

        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (JsonProperty dirProp in animEl.EnumerateObject())
        {
            var list = new List<FrameEntry>();
            foreach (JsonElement pathEl in dirProp.Value.EnumerateArray())
            {
                string rel = pathEl.GetString()!;
                string full = Path.Combine(root, rel);
                if (!File.Exists(full)) continue; // metadata lists it but it's absent -> treat as a gap
                int idx = ParseIndex(rel);
                list.Add(new FrameEntry(idx, Image.Load<Rgba32>(full)));
            }
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            byDir[dirProp.Name] = list;
        }

        return new CharacterAnimation(charName, animName, byDir);
    }

    private static int ParseIndex(string path)
    {
        Match m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+)$");
        if (!m.Success)
            throw new AssemblyException($"cannot parse frame index from '{path}'.");
        return int.Parse(m.Value);
    }
}
