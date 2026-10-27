using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Which placed objects contain the camera eye, by the triangles the view draws them with.</summary>
/// <remarks>
/// Geometry comes from the view's own <see cref="ITileMeshResolver"/>, the parts its props are drawn from, so the
/// broad-phase box and the narrow-phase triangles cannot disagree with the picture or with each other. Each
/// archetype's parts are flattened once into one positions array and one index array, and the box is measured from
/// the same vertices <see cref="TileObjectBoundsCache"/> measures, anchor-relative.
/// </remarks>
internal sealed class TileCameraContainment
{
    // The TileObjectRaycast.Pick pad: the widest footprint the catalog allows, in tiles.
    const int Pad = 8;

    readonly ITileMeshResolver _resolver;
    readonly Dictionary<string, Geometry?> _geometry = new(StringComparer.Ordinal);
    long _tests;

    public TileCameraContainment(ITileMeshResolver resolver) => _resolver = resolver;

    /// <summary>Narrow-phase tests run so far, cumulative.</summary>
    public long Tests => _tests;

    /// <summary>Fills <paramref name="inside"/> with every eligible Gameplay object that contains
    /// <paramref name="eye"/>. <paramref name="wasInside"/> is last frame's answer, which widens the exit test.</summary>
    public void Query(TileWorldView view, Vector3 eye, TileCameraFadeOptions options, TileObjectCut cut,
        HashSet<long> wasInside, HashSet<long> inside)
    {
        inside.Clear();
        TileWorldDocument doc = view.Document;
        TileWorldCatalogs catalogs = view.Catalogs;
        float ts = doc.TileSize;
        float tx = TileWorldSpace.TileX(eye.X, ts);
        float tz = TileWorldSpace.TileZ(eye.Z, ts);
        if (!float.IsFinite(tx) || !float.IsFinite(tz) || !float.IsFinite(eye.Y)) return;
        int x = (int)MathF.Floor(tx);
        int z = (int)MathF.Floor(tz);
        var search = new TileRect(x - Pad, z - Pad, Pad * 2 + 1, Pad * 2 + 1);
        float held = options.ClipRadius + options.ExitMargin;

        foreach (TileObject o in new TileObjectCandidates(doc, search))
        {
            if (!view.IsGameplayObject(o)) continue;
            string archetypeId = view.ArchetypeFor(o);
            if (catalogs.Archetype(archetypeId) is not { } archetype || !options.Eligible(archetype)) continue;
            if (GeometryFor(archetype) is not { } geometry) continue;

            Vector3 anchor = TileObjectPlacement.AnchorPosition(doc, archetype, o);
            float yaw = TileObjectPlacement.YawRadians(archetype, o.Rotation);
            if (!RayMath.ContainsObbY(eye, anchor, yaw, geometry.Min, geometry.Max, held)) continue;
            if (cut.Field is { } field && cut.ArchetypeIds?.Contains(archetypeId) == true
                && field.Contains(eye, cut.Frame))
                continue;

            _tests++;
            Vector3 local = RayMath.ToObbLocalY(eye, anchor, yaw);
            bool contains = MathF.Abs(MeshContainment.WindingNumber(geometry.Positions, geometry.Indices, local)) >= 0.5f;
            if (!contains)
            {
                float reach = wasInside.Contains(o.Id) ? held : options.ClipRadius;
                contains = MeshContainment.NearestDistanceSquared(geometry.Positions, geometry.Indices, local)
                    <= reach * reach;
            }
            if (contains) inside.Add(o.Id);
        }
    }

    Geometry? GeometryFor(TileObjectArchetype archetype)
    {
        if (_geometry.TryGetValue(archetype.Id, out Geometry? cached)) return cached;
        Geometry? built = Flatten(_resolver.Resolve(archetype));
        _geometry[archetype.Id] = built;
        return built;
    }

    static Geometry? Flatten(IReadOnlyList<GltfMeshPart>? parts)
    {
        if (parts is not { Count: > 0 }) return null;
        int vertexCount = 0, indexCount = 0;
        foreach (GltfMeshPart part in parts)
        {
            vertexCount += part.Mesh.Vertices.Length;
            indexCount += part.Mesh.Indices32.Length;
        }
        if (vertexCount == 0 || indexCount < 3) return null;

        var positions = new Vector3[vertexCount];
        var indices = new int[indexCount];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int v = 0, i = 0;
        foreach (GltfMeshPart part in parts)
        {
            int start = v;
            foreach (ModelVertex vertex in part.Mesh.Vertices)
            {
                positions[v++] = vertex.Position;
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }
            foreach (uint index in part.Mesh.Indices32) indices[i++] = start + (int)index;
        }
        return new Geometry(positions, indices, min, max);
    }

    sealed record Geometry(Vector3[] Positions, int[] Indices, Vector3 Min, Vector3 Max);
}
