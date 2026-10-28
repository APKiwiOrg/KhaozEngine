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
/// <para>The winding number is judged shell by shell, a shell being the triangles connected through shared corners,
/// and the eye is inside when it is inside any one. Summed over the whole model, a shell exported inside out
/// cancels a correctly wound one it overlaps, and the eye in the overlap would read as outside both. A single
/// connected shell whose faces disagree about which way is out is beyond this, and only the near-surface test
/// catches it.</para>
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
            // Animated foliage draws through the ground-cover path, which the fade does not reach, so it never
            // reports a fade that nothing on screen would show.
            if (view.DrawsAsAnimatedFoliage(archetypeId)) continue;
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
            bool contains = EnclosedByAnyShell(geometry, local);
            if (!contains)
            {
                float reach = wasInside.Contains(o.Id) ? held : options.ClipRadius;
                contains = MeshContainment.NearestDistanceSquared(geometry.Positions, geometry.Indices, local)
                    <= reach * reach;
            }
            if (contains) inside.Add(o.Id);
        }
    }

    static bool EnclosedByAnyShell(Geometry geometry, Vector3 local)
    {
        int[] starts = geometry.ShellStarts;
        for (int s = 0; s + 1 < starts.Length; s++)
        {
            ReadOnlySpan<int> shell = geometry.Indices.AsSpan(starts[s], starts[s + 1] - starts[s]);
            if (MathF.Abs(MeshContainment.WindingNumber(geometry.Positions, shell, local)) >= 0.5f) return true;
        }
        return false;
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
        indexCount -= indexCount % 3;
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
            uint[] partIndices = part.Mesh.Indices32;
            int whole = partIndices.Length - partIndices.Length % 3;
            for (int k = 0; k < whole && i < indices.Length; k++) indices[i++] = start + (int)partIndices[k];
        }
        if (i < indices.Length) Array.Resize(ref indices, i);
        int[] shellStarts = SortIntoShells(positions, indices);
        return new Geometry(positions, indices, shellStarts, min, max);
    }

    // Welds corners closer than this many metres, so a flat-shaded export that splits every vertex per face still
    // reads as the shells it was modelled as.
    const float WeldCell = 1e-4f;

    /// <summary>Reorders <paramref name="indices"/> so each shell's triangles are contiguous and returns the index
    /// offset where each shell starts, with the total length appended. Corners are welded by position, because index
    /// connectivity alone would make every face of a flat-shaded model its own shell.</summary>
    static int[] SortIntoShells(Vector3[] positions, int[] indices)
    {
        var corners = new Dictionary<(long X, long Y, long Z), int>();
        var welded = new int[positions.Length];
        for (int v = 0; v < positions.Length; v++)
        {
            Vector3 p = positions[v] / WeldCell;
            (long, long, long) key = ((long)MathF.Round(p.X), (long)MathF.Round(p.Y), (long)MathF.Round(p.Z));
            if (!corners.TryGetValue(key, out int id)) corners.Add(key, id = corners.Count);
            welded[v] = id;
        }

        var parent = new int[corners.Count];
        for (int c = 0; c < parent.Length; c++) parent[c] = c;
        int triangles = indices.Length / 3;
        for (int t = 0; t < triangles; t++)
        {
            int a = welded[indices[t * 3]];
            Union(parent, a, welded[indices[t * 3 + 1]]);
            Union(parent, a, welded[indices[t * 3 + 2]]);
        }

        var shellOf = new Dictionary<int, int>();
        var triangleShell = new int[triangles];
        var counts = new List<int>();
        for (int t = 0; t < triangles; t++)
        {
            int root = Find(parent, welded[indices[t * 3]]);
            if (!shellOf.TryGetValue(root, out int shell))
            {
                shellOf.Add(root, shell = counts.Count);
                counts.Add(0);
            }
            triangleShell[t] = shell;
            counts[shell]++;
        }

        var starts = new int[counts.Count + 1];
        for (int s = 0; s < counts.Count; s++) starts[s + 1] = starts[s] + counts[s] * 3;
        var cursor = (int[])starts.Clone();
        var sorted = new int[indices.Length];
        for (int t = 0; t < triangles; t++)
        {
            int at = cursor[triangleShell[t]];
            sorted[at] = indices[t * 3];
            sorted[at + 1] = indices[t * 3 + 1];
            sorted[at + 2] = indices[t * 3 + 2];
            cursor[triangleShell[t]] = at + 3;
        }
        Array.Copy(sorted, indices, indices.Length);
        return starts;
    }

    static int Find(int[] parent, int x)
    {
        while (parent[x] != x) x = parent[x] = parent[parent[x]];
        return x;
    }

    static void Union(int[] parent, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a != b) parent[b] = a;
    }

    sealed record Geometry(Vector3[] Positions, int[] Indices, int[] ShellStarts, Vector3 Min, Vector3 Max);
}
