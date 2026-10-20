using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Counts the generator states independently of native results. Accepted and rejected come from the actual
/// legacy sampler over the owned candidates.</summary>
internal sealed record FoliageExpectations(long Layers, long RasterBytes, long StoredChunks, long SourceOwners, long Models,
    long OwnedCandidates, long Accepted, long Rejected);

/// <summary>The closed provenance comparison object of the private foliage contract. Every path is extraction relative.</summary>
internal sealed record FoliageComparisonPaths(string NativeDocumentPath, string NativeTiledManifestPath, string FoliageManifestPath,
    string? BuildingManifestPath, FoliageExpectations Expectations);

/// <summary>One compatibility row: the native support surface derived from one source plane.</summary>
internal sealed record FoliageSupportRow(int SourcePlane, string NativeSurfaceId);
internal sealed record FoliageModelRow(string SourceArchetypeId, string NativeAssetId);
internal sealed record FoliageMaterialRow(ushort SourceMaterialId, string NativeResourceId, MapFoliageMaterialKind Kind);

internal sealed record FoliageLayerRow(string SourceLayerId, string NativeLayerId, string SupportSurfaceId,
    IReadOnlyList<FoliageSupportRow> CompatibilitySupports, IReadOnlyList<FoliageModelRow> Models,
    IReadOnlyList<FoliageMaterialRow> Materials, IReadOnlyList<string> RuleIds, IReadOnlyList<string> MaskIds,
    IReadOnlyList<long> OwnerIds);

/// <summary>A clearing target, never seated provenance.</summary>
internal sealed record FoliageTargetRow(string NativeLayerId, string SupportSurfaceId);

internal sealed record FoliageOwnerRow(long SourceObjectId, int SourcePlane, string SourceArchetypeId, MapPrefabReference NativeOwner,
    string SeatedSurfaceId, IReadOnlyList<FoliageTargetRow> Targets, IReadOnlyList<string> RuleIds, IReadOnlyList<string> MaskIds);

internal sealed record FoliageManifest(string GeneratorPath, IReadOnlyList<string> Helpers, IReadOnlyList<FoliageLayerRow> Layers,
    IReadOnlyList<FoliageOwnerRow> Owners);

/// <summary>Strict readers for the closed private foliage shapes. Every object is closed, every member is required and
/// duplicate or case-variant members refuse before any value is read. Refusal reasons are fixed public text.</summary>
internal static class ShippedFoliageManifest
{
    internal const string GeneratorPath = "generator/build-scaffold.py";
    internal const string CandidateConvention = "world-lattice-v1";

    internal static FoliageComparisonPaths Comparison(JsonElement comparison)
    {
        UniqueMembers(comparison);
        Shape(comparison, "schemaVersion", "nativeDocumentPath", "nativeTiledManifestPath", "foliageManifestPath",
            "buildingManifestPath", "expectations");
        Schema(comparison, "unsupported foliage comparison schema");
        JsonElement expectations = comparison.GetProperty("expectations");
        Shape(expectations, "layers", "rasterBytes", "storedChunks", "sourceOwners", "models", "ownedCandidates", "accepted", "rejected");
        long Count(string name)
        {
            JsonElement value = expectations.GetProperty(name);
            Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long count) && count >= 0, "invalid foliage expectation");
            return value.GetInt64();
        }
        var counts = new FoliageExpectations(Count("layers"), Count("rasterBytes"), Count("storedChunks"), Count("sourceOwners"),
            Count("models"), Count("ownedCandidates"), Count("accepted"), Count("rejected"));
        string native = Path(comparison, "nativeDocumentPath"), tiled = Path(comparison, "nativeTiledManifestPath");
        string manifest = Path(comparison, "foliageManifestPath");
        string? building = comparison.GetProperty("buildingManifestPath").ValueKind == JsonValueKind.Null ? null : Path(comparison, "buildingManifestPath");
        return new(native, tiled, manifest, building, counts);
    }

    internal static FoliageManifest Read(JsonElement manifest)
    {
        UniqueMembers(manifest);
        Shape(manifest, "schemaVersion", "generator", "layers", "owners");
        Schema(manifest, "unsupported foliage manifest schema");
        JsonElement generator = manifest.GetProperty("generator");
        Shape(generator, "path", "helpers");
        string path = Path(generator, "path");
        Require(path == GeneratorPath, "missing retained generator");
        string[] helpers = Strings(generator, "helpers").Select(SafePath).ToArray();
        Require(!helpers.Contains(path, StringComparer.Ordinal), "duplicate generator input");
        FoliageLayerRow[] layers = Array(manifest, "layers").Select(Layer).ToArray();
        Require(layers.Length != 0, "missing foliage layer correspondence");
        Require(Unique(layers.Select(l => l.SourceLayerId)) && Unique(layers.Select(l => l.NativeLayerId)), "duplicate foliage layer correspondence");
        FoliageOwnerRow[] owners = Array(manifest, "owners").Select(Owner).ToArray();
        Require(owners.Select(o => o.SourceObjectId).Distinct().Count() == owners.Length && owners.Select(o => o.NativeOwner).Distinct().Count() == owners.Length,
            "duplicate foliage owner correspondence");
        return new(path, System.Array.AsReadOnly(helpers), System.Array.AsReadOnly(layers), System.Array.AsReadOnly(owners));
    }

    static FoliageLayerRow Layer(JsonElement row)
    {
        Shape(row, "sourceLayerId", "nativeLayerId", "support", "compatibilitySupports", "models", "materials", "ruleIds", "maskIds",
            "ownerIds", "candidateConvention");
        Require(Text(row, "candidateConvention") == CandidateConvention, "unknown foliage candidate convention");
        FoliageSupportRow[] supports = Array(row, "compatibilitySupports").Select(s =>
        {
            Shape(s, "sourcePlane", "nativeSurfaceId");
            return new FoliageSupportRow(Int(s, "sourcePlane", 0, 255), Text(s, "nativeSurfaceId"));
        }).ToArray();
        Require(supports.Length != 0 && Unique(supports.Select(s => s.NativeSurfaceId)), "duplicate or missing compatibility support row");
        FoliageModelRow[] models = Array(row, "models").Select(m =>
        {
            Shape(m, "sourceArchetypeId", "nativeAssetId");
            return new FoliageModelRow(Text(m, "sourceArchetypeId"), Text(m, "nativeAssetId"));
        }).ToArray();
        Require(models.Length != 0 && Unique(models.Select(m => m.SourceArchetypeId)) && Unique(models.Select(m => m.NativeAssetId)),
            "duplicate or missing foliage model row");
        FoliageMaterialRow[] materials = Array(row, "materials").Select(m =>
        {
            Shape(m, "sourceMaterialId", "nativeResourceId", "kind");
            string kind = Text(m, "kind");
            Require(kind is "Ordinary" or "Water", "unknown foliage material kind");
            return new FoliageMaterialRow((ushort)Int(m, "sourceMaterialId", 1, ushort.MaxValue), Text(m, "nativeResourceId"),
                kind == "Water" ? MapFoliageMaterialKind.Water : MapFoliageMaterialKind.Ordinary);
        }).ToArray();
        Require(materials.Length != 0 && materials.Zip(materials.Skip(1)).All(p => p.First.SourceMaterialId < p.Second.SourceMaterialId),
            "foliage material rows must be unique and ascending");
        long[] owners = Strings(row, "ownerIds").Select(ObjectId).ToArray();
        return new(Text(row, "sourceLayerId"), Text(row, "nativeLayerId"), SurfaceBinding(row.GetProperty("support")),
            System.Array.AsReadOnly(supports), System.Array.AsReadOnly(models), System.Array.AsReadOnly(materials),
            System.Array.AsReadOnly(Strings(row, "ruleIds")), System.Array.AsReadOnly(Strings(row, "maskIds")), System.Array.AsReadOnly(owners));
    }

    static FoliageOwnerRow Owner(JsonElement row)
    {
        Shape(row, "sourceObjectId", "sourcePlane", "sourceArchetypeId", "nativeOwner", "seatedSupport", "targets", "ruleIds", "maskIds");
        JsonElement id = row.GetProperty("sourceObjectId");
        Require(id.ValueKind == JsonValueKind.String, "invalid foliage owner identity");
        JsonElement native = row.GetProperty("nativeOwner");
        Shape(native, "scope", "kind", "id");
        Require(Text(native, "scope") == "World", "foliage owners must be world references");
        string kindText = Text(native, "kind");
        Require(kindText is "Placement" or "Surface" or "Record" or "Marker", "unknown foliage owner kind");
        MapPrefabChildKind kind = Enum.Parse<MapPrefabChildKind>(kindText);
        FoliageTargetRow[] targets = Array(row, "targets").Select(t =>
        {
            Shape(t, "nativeLayerId", "support");
            return new FoliageTargetRow(Text(t, "nativeLayerId"), SurfaceBinding(t.GetProperty("support")));
        }).ToArray();
        Require(Unique(targets.Select(t => t.NativeLayerId)), "duplicate foliage clearing target");
        return new(ObjectId(id.GetString()!), Int(row, "sourcePlane", 0, 255), Text(row, "sourceArchetypeId"),
            new(MapPrefabReferenceScope.World, kind, Text(native, "id")), SurfaceBinding(row.GetProperty("seatedSupport")),
            System.Array.AsReadOnly(targets), System.Array.AsReadOnly(Strings(row, "ruleIds")), System.Array.AsReadOnly(Strings(row, "maskIds")));
    }

    // Private imports bind their source surface. A Space binding or any other shape refuses.
    static string SurfaceBinding(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("kind", out JsonElement kind) &&
            kind.ValueKind == JsonValueKind.String && kind.GetString() == "Surface", "private foliage imports must bind a Surface");
        Shape(value, "kind", "surfaceId");
        return Text(value, "surfaceId");
    }

    static long ObjectId(string? text)
    {
        Require(text is { Length: > 0 and <= 19 } && text[0] is >= '1' and <= '9' && text.All(c => c is >= '0' and <= '9') &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0, "invalid foliage owner identity");
        return long.Parse(text!, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    internal static void Require(bool ok, string reason) { if (!ok) throw new InvalidDataException(reason); }

    static void Schema(JsonElement value, string reason)
    {
        JsonElement version = value.GetProperty("schemaVersion");
        Require(version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number) && number == 1, reason);
    }

    static bool Unique(IEnumerable<string> values)
    {
        string[] all = values.ToArray();
        return all.Distinct(StringComparer.Ordinal).Count() == all.Length;
    }

    static JsonElement[] Array(JsonElement value, string name)
    {
        JsonElement array = value.GetProperty(name);
        Require(array.ValueKind == JsonValueKind.Array, "incomplete comparison object");
        return array.EnumerateArray().ToArray();
    }

    internal static string Text(JsonElement value, string name)
    {
        JsonElement text = value.GetProperty(name);
        Require(text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString()), "missing comparison text");
        return text.GetString()!;
    }

    static int Int(JsonElement value, string name, int min, int max)
    {
        JsonElement number = value.GetProperty(name);
        Require(number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int result) && result >= min && result <= max,
            "invalid comparison number");
        return number.GetInt32();
    }

    static string[] Strings(JsonElement value, string name)
    {
        string[] strings = Array(value, name).Select(v =>
        {
            Require(v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()), "invalid comparison references");
            return v.GetString()!;
        }).ToArray();
        Require(Unique(strings), "invalid comparison references");
        return strings;
    }

    static string Path(JsonElement value, string name) => SafePath(Text(value, name));

    // Attested extraction-relative paths only. No root, drive, back slash, control character, empty, dot or dot-dot part.
    internal static string SafePath(string path)
    {
        Require(!string.IsNullOrWhiteSpace(path) && !System.IO.Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') &&
            !path.Any(char.IsControl) && path.Split('/').All(p => p.Length != 0 && p is not "." and not ".."), "unsafe attested foliage path");
        return path;
    }

    internal static void Shape(JsonElement value, params string[] fields) => Require(value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal)),
        "incomplete comparison object");

    internal static void UniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "duplicate or case-variant JSON member");
                UniqueMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) UniqueMembers(item);
    }
}
