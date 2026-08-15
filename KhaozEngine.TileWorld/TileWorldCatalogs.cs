using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Content;
using KhaozEngine.Serialization;

namespace KhaozEngine.TileWorld;

/// <summary>What a ground material is, for the systems that care about the difference.</summary>
public enum GroundMaterialKind
{
    /// <summary>Ordinary walkable ground.</summary>
    Ground,
    /// <summary>Water, drawn and treated differently from ordinary ground.</summary>
    Water,
}

/// <summary>A ground material referenced by underlay/overlay id. Colour drives the v1 vertex-colour renderer,
/// <see cref="Texture"/> is reserved for a later texture path.</summary>
public sealed class GroundMaterial
{
    /// <summary>Catalog id, referenced from the underlay and overlay layers. 0 is reserved for void.</summary>
    public ushort Id { get; set; }
    /// <summary>Authoring name, quoted in error messages and shown in the editor.</summary>
    public string Name { get; set; } = "";
    /// <summary><c>#rrggbb</c>.</summary>
    public string Color { get; set; } = "#ffffff";
    /// <summary>Texture reference for the later texture path, null when the material is colour only.</summary>
    public string? Texture { get; set; }
    /// <summary>Ordinary ground or water.</summary>
    public GroundMaterialKind Kind { get; set; } = GroundMaterialKind.Ground;
}

/// <summary>How an archetype contributes to the derived collision map (the collision baker reads this, it is
/// never authored per tile).</summary>
public enum TileCollisionKind
{
    /// <summary>Blocks nothing.</summary>
    None,
    /// <summary>Blocks the whole tile it stands on.</summary>
    Solid,
    /// <summary>Blocks one tile edge, the one its rotation faces.</summary>
    Wall,
    /// <summary>Blocks the two edges meeting at the corner its rotation faces.</summary>
    WallCorner,
    /// <summary>Blocks the tile diagonally, along the diagonal its rotation selects.</summary>
    Diagonal,
}

/// <summary>An object archetype referenced by id from <see cref="TileObject.ArchetypeId"/>.</summary>
public sealed class TileObjectArchetype
{
    /// <summary>Catalog-unique id, the value a <see cref="TileObject"/> stores.</summary>
    public string Id { get; set; } = "";
    /// <summary>Authoring name shown in the editor.</summary>
    public string Name { get; set; } = "";
    /// <summary>Mesh this archetype draws with, resolved by the game's content pipeline.</summary>
    public string MeshRef { get; set; } = "";
    /// <summary>Unrotated footprint width in tiles, east.</summary>
    public int SizeX { get; set; } = 1;
    /// <summary>Unrotated footprint depth in tiles, north.</summary>
    public int SizeZ { get; set; } = 1;
    /// <summary>What this archetype blocks once baked.</summary>
    public TileCollisionKind CollisionKind { get; set; } = TileCollisionKind.None;
    /// <summary>True when the object is a roof, hidden while the camera subject stands indoors.</summary>
    public bool IsRoof { get; set; }
    /// <summary>True when the object can be clicked for an action.</summary>
    public bool Interactive { get; set; }
    /// <summary>Extra yaw in degrees applied on top of the instance rotation, for meshes authored off-axis.</summary>
    public float YawOffsetDegrees { get; set; }
    /// <summary>Free-form authoring tags, null when none.</summary>
    public List<string>? Tags { get; set; }
}

/// <summary>Footprint helpers. Rotation swaps X and Z on odd quarter turns, the anchor stays the SW tile.</summary>
public static class TileFootprint
{
    /// <summary>The archetype's footprint size after <paramref name="rotation"/> quarter turns.</summary>
    public static (int SizeX, int SizeZ) Rotated(TileObjectArchetype a, int rotation) =>
        (rotation & 1) == 0 ? (a.SizeX, a.SizeZ) : (a.SizeZ, a.SizeX);

    /// <summary>The world tile rect an instance covers, anchored at the SW tile (x, z).</summary>
    public static TileRect Of(TileObjectArchetype a, int x, int z, int rotation)
    {
        (int sx, int sz) = Rotated(a, rotation);
        return new TileRect(x, z, sx, sz);
    }
}

/// <summary>The ground materials and object archetypes a world references by id. Game content, loaded from
/// JSON files named by <see cref="TileWorldDocument.CatalogPaths"/>, never stored in the world.</summary>
public sealed class TileWorldCatalogs
{
    readonly Dictionary<ushort, GroundMaterial> _materials = new();
    readonly Dictionary<string, TileObjectArchetype> _archetypes = new(StringComparer.Ordinal);

    /// <summary>Every ground material, by id.</summary>
    public IReadOnlyDictionary<ushort, GroundMaterial> Materials => _materials;
    /// <summary>Every object archetype, by id.</summary>
    public IReadOnlyDictionary<string, TileObjectArchetype> Archetypes => _archetypes;

    /// <summary>The material with this id, or null when the catalogs do not define it.</summary>
    public GroundMaterial? Material(ushort id) => _materials.TryGetValue(id, out GroundMaterial? m) ? m : null;
    /// <summary>The archetype with this id, or null when the catalogs do not define it.</summary>
    public TileObjectArchetype? Archetype(string id) => _archetypes.TryGetValue(id, out TileObjectArchetype? a) ? a : null;

    /// <summary>Catalogs with nothing in them.</summary>
    public static TileWorldCatalogs Empty() => new();

    sealed class CatalogFile
    {
        public List<GroundMaterial>? Materials { get; set; }
        public List<TileObjectArchetype>? Archetypes { get; set; }
    }

    static readonly JsonSerializerOptions ReadOptions = CreateReadOptions();

    static JsonSerializerOptions CreateReadOptions()
    {
        var o = new JsonSerializerOptions(Jsonc.Options) { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    /// <summary>Parses one catalog file's JSON (JSONC tolerated), schema-checked. Errors name
    /// <paramref name="sourceName"/>.</summary>
    public static TileWorldCatalogs LoadJson(string json, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(json);
        ValidationReport report = JsonSchemaValidator.Validate(json, TileWorldSchema.GetCatalogJson());
        if (!report.IsValid)
            throw new TileWorldException($"{sourceName}: catalog does not match the schema: {string.Join("; ", report.Errors)}");
        CatalogFile file;
        try { file = JsonSerializer.Deserialize<CatalogFile>(json, ReadOptions) ?? new CatalogFile(); }
        catch (JsonException ex) { throw new TileWorldException($"{sourceName}: {ex.Message}", ex); }

        var c = new TileWorldCatalogs();
        foreach (GroundMaterial m in file.Materials ?? new()) c.AddMaterial(m, sourceName);
        foreach (TileObjectArchetype a in file.Archetypes ?? new()) c.AddArchetype(a, sourceName);
        return c;
    }

    /// <summary>Loads and merges catalog files. A duplicate id across files names both.</summary>
    public static TileWorldCatalogs Load(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var merged = new TileWorldCatalogs();
        foreach (string path in paths)
        {
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new TileWorldException($"{path}: cannot read catalog. {ex.Message}", ex);
            }
            merged.MergeFrom(LoadJson(json, path), path);
        }
        return merged;
    }

    /// <summary>Merges already-loaded catalogs into one. A duplicate id across parts throws.</summary>
    public static TileWorldCatalogs Merge(params TileWorldCatalogs[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var merged = new TileWorldCatalogs();
        for (int i = 0; i < parts.Length; i++) merged.MergeFrom(parts[i], $"catalog #{i}");
        return merged;
    }

    readonly Dictionary<ushort, string> _materialSources = new();
    readonly Dictionary<string, string> _archetypeSources = new(StringComparer.Ordinal);

    void MergeFrom(TileWorldCatalogs other, string sourceName)
    {
        foreach (GroundMaterial m in other._materials.Values) AddMaterial(m, other._materialSources.GetValueOrDefault(m.Id, sourceName));
        foreach (TileObjectArchetype a in other._archetypes.Values) AddArchetype(a, other._archetypeSources.GetValueOrDefault(a.Id, sourceName));
    }

    void AddMaterial(GroundMaterial m, string source)
    {
        if (m.Id == 0) throw new TileWorldException($"{source}: material id 0 is reserved for void");
        if (_materials.ContainsKey(m.Id))
            throw new TileWorldException($"{source}: material {m.Id} ('{m.Name}') is already defined in {_materialSources[m.Id]}");
        _materials.Add(m.Id, m);
        _materialSources[m.Id] = source;
    }

    void AddArchetype(TileObjectArchetype a, string source)
    {
        if (string.IsNullOrWhiteSpace(a.Id)) throw new TileWorldException($"{source}: an archetype has no id");
        if (_archetypes.ContainsKey(a.Id))
            throw new TileWorldException($"{source}: archetype '{a.Id}' is already defined in {_archetypeSources[a.Id]}");
        _archetypes.Add(a.Id, a);
        _archetypeSources[a.Id] = source;
    }

    /// <summary>The engine's minimal test/greybox catalogs: six materials and eleven archetypes covering every
    /// <see cref="TileCollisionKind"/>. Games ship their own.</summary>
    public static TileWorldCatalogs Greybox()
    {
        var c = new TileWorldCatalogs();
        void Mat(ushort id, string name, string color, GroundMaterialKind kind = GroundMaterialKind.Ground) =>
            c.AddMaterial(new GroundMaterial { Id = id, Name = name, Color = color, Kind = kind }, "greybox");
        void Arch(string id, TileCollisionKind kind, int sx = 1, int sz = 1, bool roof = false, bool interactive = false) =>
            c.AddArchetype(new TileObjectArchetype
            {
                Id = id, Name = id, MeshRef = $"greybox/{id}.glb", SizeX = sx, SizeZ = sz,
                CollisionKind = kind, IsRoof = roof, Interactive = interactive,
            }, "greybox");

        Mat(1, "grass", "#4d8a3a");
        Mat(2, "dirt", "#8a6a3a");
        Mat(3, "stone", "#7a7a7a");
        Mat(4, "water", "#2a5a9a", GroundMaterialKind.Water);
        Mat(5, "wood_floor", "#9a6a3a");
        Mat(6, "road", "#6a6a5a");

        Arch("wall", TileCollisionKind.Wall);
        Arch("wall_corner", TileCollisionKind.WallCorner);
        Arch("doorway", TileCollisionKind.None);
        Arch("fence", TileCollisionKind.Wall);
        Arch("tree", TileCollisionKind.Solid);
        Arch("rock_large", TileCollisionKind.Solid, 2, 2);
        Arch("bush", TileCollisionKind.None);
        Arch("roof_flat", TileCollisionKind.None, roof: true);
        Arch("stairs", TileCollisionKind.Solid, interactive: true);
        Arch("bank_booth", TileCollisionKind.Solid, interactive: true);
        Arch("diag_wall", TileCollisionKind.Diagonal);
        return c;
    }
}
