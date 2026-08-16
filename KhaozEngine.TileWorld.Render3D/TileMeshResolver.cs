using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Turns an object archetype into the mesh parts that draw it. A game implements this over its own
/// content pipeline, keyed off the archetype's mesh reference. Returning null means "no mesh for this
/// archetype", which the view answers with a placeholder box and one log line per archetype rather than a
/// throw, so a half-authored catalog still renders.</summary>
public interface ITileMeshResolver
{
    /// <summary>The parts that draw this archetype, or null when the resolver has no mesh for it.</summary>
    IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype);
}

/// <summary>A resolver with no content behind it: one procedural vertex-coloured box per archetype, sized from
/// the archetype's footprint and shaped by its collision kind, so a world renders recognisably before any mesh
/// is authored. Boxes are centred on the footprint in x and z with their base at y 0, matching the anchor the
/// object-to-prop pass produces, and a wall hugs the WEST edge of its footprint (-x) so instance rotation 0
/// reads as a west wall, with a corner wall adding the north edge, which is -z rather than +z because world z
/// is minus tile z (<see cref="TileWorldSpace"/>). Only the footprint extent scales with the tile size: every
/// thickness and height here (wall, roof, tree, post, the default box) is an absolute measurement in metres, so
/// a bigger tile makes a wider wall, never a taller one. Used by the tests and as the engine's default
/// placeholder.</summary>
public sealed class GreyboxMeshResolver : ITileMeshResolver
{
    /// <summary>Thickness in metres of a wall slab, across the edge it sits on.</summary>
    public const float WallThickness = 0.15f;
    /// <summary>Height in metres of a wall, a corner wall and a diagonal post.</summary>
    public const float WallHeight = 2.5f;
    /// <summary>Thickness in metres of a roof slab, which hangs directly above the walls.</summary>
    public const float RoofThickness = 0.2f;
    /// <summary>Side in metres of the square post standing in for a diagonal wall.</summary>
    public const float DiagonalPostWidth = 0.3f;
    /// <summary>Width in metres of the tree box.</summary>
    public const float TreeWidth = 0.6f;
    /// <summary>Height in metres of the tree box.</summary>
    public const float TreeHeight = 3f;
    /// <summary>Height in metres of a rock, which fills its whole footprint.</summary>
    public const float RockHeight = 1f;
    /// <summary>Height in metres of every archetype with no shape rule of its own.</summary>
    public const float DefaultHeight = 1f;

    readonly float _tileSize;
    readonly Dictionary<string, IReadOnlyList<GltfMeshPart>> _cache = new(StringComparer.Ordinal);

    /// <summary>A resolver whose footprint-sized boxes use this tile size in metres.</summary>
    public GreyboxMeshResolver(float tileSize = TileWorldDocument.DefaultTileSize) => _tileSize = tileSize;

    /// <summary>The single-part box for this archetype, built once and handed back on every later call. Never
    /// null, because the greybox resolver has a shape for everything. The list is read-only, so a caller cannot
    /// write through it into the shared cache.</summary>
    public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        if (_cache.TryGetValue(archetype.Id, out IReadOnlyList<GltfMeshPart>? cached)) return cached;

        // A default GltfMaterialMaps is the untextured form: the model path then lights the vertex colour alone.
        IReadOnlyList<GltfMeshPart> parts = Array.AsReadOnly(new[] { new GltfMeshPart(BuildMesh(archetype), default) });
        _cache[archetype.Id] = parts;
        return parts;
    }

    /// <summary>The greybox palette entry for an archetype id: a deterministic grey, brown or green, so the same
    /// id is the same colour on every machine and every rebuild.</summary>
    public static Vector4 ColorOf(string archetypeId)
    {
        ArgumentNullException.ThrowIfNull(archetypeId);
        uint h = Hash(archetypeId);
        Vector3 tone = Palette[(int)(h % (uint)Palette.Length)];
        float shade = 0.85f + ((h >> 16) & 0xFF) / 255f * 0.3f;
        return new Vector4(Vector3.Clamp(tone * shade, Vector3.Zero, Vector3.One), 1f);
    }

    /// <summary>An axis-aligned box between two corners as 12 triangles, each face with its own outward normal
    /// and four vertices of its own, so the faces stay flat-shaded.</summary>
    public static GltfMesh Box(Vector3 min, Vector3 max, Vector4 color)
    {
        var vertices = new ModelVertex[24];
        var indices = new ushort[36];
        int v = 0, i = 0;

        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            int start = v;
            vertices[v++] = new ModelVertex(a, n, color, Vector2.Zero);
            vertices[v++] = new ModelVertex(b, n, color, Vector2.Zero);
            vertices[v++] = new ModelVertex(c, n, color, Vector2.Zero);
            vertices[v++] = new ModelVertex(d, n, color, Vector2.Zero);
            indices[i++] = (ushort)start;
            indices[i++] = (ushort)(start + 1);
            indices[i++] = (ushort)(start + 2);
            indices[i++] = (ushort)start;
            indices[i++] = (ushort)(start + 2);
            indices[i++] = (ushort)(start + 3);
        }

        Face(new(min.X, min.Y, min.Z), new(min.X, min.Y, max.Z), new(min.X, max.Y, max.Z), new(min.X, max.Y, min.Z), -Vector3.UnitX);
        Face(new(max.X, min.Y, max.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(max.X, max.Y, max.Z), Vector3.UnitX);
        Face(new(min.X, min.Y, max.Z), new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), -Vector3.UnitY);
        Face(new(min.X, max.Y, min.Z), new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z), new(max.X, max.Y, min.Z), Vector3.UnitY);
        Face(new(max.X, min.Y, min.Z), new(min.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z), -Vector3.UnitZ);
        Face(new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z), Vector3.UnitZ);

        return new GltfMesh(vertices, indices);
    }

    // Exact ids, not a prefix match. A prefix silently captures anything a game's catalogs happen to name
    // alike (a "rockery_wall" is not a rock), and the greybox catalogs are a closed set, so matching the ids
    // outright says exactly which archetypes get the shape and stops guessing about the rest.
    static readonly HashSet<string> TreeIds = new(StringComparer.Ordinal) { "tree" };
    static readonly HashSet<string> RockIds = new(StringComparer.Ordinal) { "rock", "rock_large" };

    // Greys, browns and greens: enough separation to tell archetypes apart, dull enough to read as greybox.
    static readonly Vector3[] Palette =
    {
        new(0.62f, 0.62f, 0.60f),
        new(0.45f, 0.45f, 0.47f),
        new(0.55f, 0.40f, 0.26f),
        new(0.38f, 0.28f, 0.18f),
        new(0.34f, 0.52f, 0.28f),
        new(0.24f, 0.38f, 0.22f),
    };

    GltfMesh BuildMesh(TileObjectArchetype a)
    {
        float halfX = a.SizeX * 0.5f * _tileSize, halfZ = a.SizeZ * 0.5f * _tileSize;
        Vector4 color = ColorOf(a.Id);

        // A roof covers the whole footprint and hangs above wall height, so the roof rule has something to hide.
        if (a.IsRoof)
            return Box(new Vector3(-halfX, WallHeight, -halfZ), new Vector3(halfX, WallHeight + RoofThickness, halfZ), color);

        switch (a.CollisionKind)
        {
            case TileCollisionKind.Wall:
                return WestSlab(halfX, halfZ, color);
            case TileCollisionKind.WallCorner:
                return Combine(WestSlab(halfX, halfZ, color), NorthSlab(halfX, halfZ, color));
            case TileCollisionKind.Diagonal:
                // A slab along the diagonal is not an axis-aligned box, so the greybox stands a post at the
                // tile centre instead. It reads as "something blocks this tile" without faking the shape.
                float post = DiagonalPostWidth * 0.5f;
                return Box(new Vector3(-post, 0f, -post), new Vector3(post, WallHeight, post), color);
        }

        if (TreeIds.Contains(a.Id))
        {
            float trunk = TreeWidth * 0.5f;
            return Box(new Vector3(-trunk, 0f, -trunk), new Vector3(trunk, TreeHeight, trunk), color);
        }
        if (RockIds.Contains(a.Id))
            return Box(new Vector3(-halfX, 0f, -halfZ), new Vector3(halfX, RockHeight, halfZ), color);

        return Box(new Vector3(-halfX, 0f, -halfZ), new Vector3(halfX, DefaultHeight, halfZ), color);
    }

    static GltfMesh WestSlab(float halfX, float halfZ, Vector4 color) =>
        Box(new Vector3(-halfX, 0f, -halfZ), new Vector3(-halfX + WallThickness, WallHeight, halfZ), color);

    // North is MINUS z in world space (TileWorldSpace), so the north slab hugs the -z face of the footprint, not
    // the +z one a tile-space reading of the axis would suggest.
    static GltfMesh NorthSlab(float halfX, float halfZ, Vector4 color) =>
        Box(new Vector3(-halfX, 0f, -halfZ), new Vector3(halfX, WallHeight, -halfZ + WallThickness), color);

    static GltfMesh Combine(GltfMesh first, GltfMesh second)
    {
        var vertices = new ModelVertex[first.Vertices.Length + second.Vertices.Length];
        first.Vertices.CopyTo(vertices, 0);
        second.Vertices.CopyTo(vertices, first.Vertices.Length);

        var indices = new ushort[first.Indices.Length + second.Indices.Length];
        first.Indices.CopyTo(indices, 0);
        for (int i = 0; i < second.Indices.Length; i++)
            indices[first.Indices.Length + i] = (ushort)(second.Indices[i] + first.Vertices.Length);
        return new GltfMesh(vertices, indices);
    }

    static uint Hash(string s)
    {
        uint h = 2166136261u;
        unchecked
        {
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
        }
        return h;
    }
}
