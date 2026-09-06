using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

internal sealed class FoliagePatchLayout
{
    internal FoliageInstance[] Instances { get; }
    internal FoliagePatch[] Patches { get; }

    FoliagePatchLayout(FoliageInstance[] instances, FoliagePatch[] patches)
    {
        Instances = instances;
        Patches = patches;
    }

    internal static FoliagePatchLayout Build(ReadOnlySpan<FoliageInstance> instances,
        Func<MeshHandle, MeshBounds?> boundsForMesh, float patchSize = 8f)
    {
        ArgumentNullException.ThrowIfNull(boundsForMesh);
        if (!float.IsFinite(patchSize) || patchSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(patchSize), "Foliage patch size must be finite and positive.");

        var entries = new List<Entry>(instances.Length);
        var meshBounds = new Dictionary<(int, int), MeshBounds?>();
        for (int i = 0; i < instances.Length; i++)
        {
            FoliageInstance instance = instances[i];
            if (!float.IsFinite(instance.ThinningRank) || instance.ThinningRank < 0f || instance.ThinningRank >= 1f)
                throw new ArgumentException("Foliage ranks must be finite, at least zero and less than one.", nameof(instances));
            ValidateTransform(instance.Transform);
            var meshKey = (instance.Mesh.Index, instance.Mesh.Generation);
            if (!meshBounds.TryGetValue(meshKey, out MeshBounds? local))
            {
                local = boundsForMesh(instance.Mesh);
                meshBounds.Add(meshKey, local);
            }
            if (local is not MeshBounds bounds) continue;

            MeshBounds world = TransformBounds(bounds, instance.Transform, out float height);
            // Double division keeps tiny cells and large finite coordinates away from integer overflow.
            var key = new PatchKey(Math.Floor((double)instance.Transform.M41 / patchSize),
                Math.Floor((double)instance.Transform.M43 / patchSize), meshKey.Index, meshKey.Generation);
            entries.Add(new Entry(key, instance, world, height, i));
        }

        entries.Sort(static (a, b) =>
        {
            int comparison = a.Key.X.CompareTo(b.Key.X);
            if (comparison == 0) comparison = a.Key.Z.CompareTo(b.Key.Z);
            if (comparison == 0) comparison = a.Key.MeshIndex.CompareTo(b.Key.MeshIndex);
            if (comparison == 0) comparison = a.Key.MeshGeneration.CompareTo(b.Key.MeshGeneration);
            if (comparison == 0) comparison = a.Instance.ThinningRank.CompareTo(b.Instance.ThinningRank);
            return comparison != 0 ? comparison : a.InputOrder.CompareTo(b.InputOrder);
        });

        var ordered = new FoliageInstance[entries.Count];
        var patches = new List<FoliagePatch>();
        int start = 0;
        while (start < entries.Count)
        {
            Entry first = entries[start];
            Vector3 min = first.Bounds.Min, max = first.Bounds.Max;
            Vector2 rootMin = Root(first.Instance), rootMax = rootMin;
            float height = first.Height;
            int end = start;
            do
            {
                Entry entry = entries[end];
                ordered[end] = entry.Instance;
                min = Vector3.Min(min, entry.Bounds.Min);
                max = Vector3.Max(max, entry.Bounds.Max);
                Vector2 root = Root(entry.Instance);
                rootMin = Vector2.Min(rootMin, root);
                rootMax = Vector2.Max(rootMax, root);
                height = MathF.Max(height, entry.Height);
                end++;
            } while (end < entries.Count && entries[end].Key == first.Key);

            patches.Add(new FoliagePatch(first.Instance.Mesh, start, end - start,
                new MeshBounds(min, max), rootMin, rootMax, height));
            start = end;
        }
        return new FoliagePatchLayout(ordered, patches.ToArray());
    }

    static Vector2 Root(in FoliageInstance instance) => new(instance.Transform.M41, instance.Transform.M43);

    static void ValidateTransform(in Matrix4x4 m)
    {
        if (!float.IsFinite(m.M11) || !float.IsFinite(m.M12) || !float.IsFinite(m.M13) || !float.IsFinite(m.M14) ||
            !float.IsFinite(m.M21) || !float.IsFinite(m.M22) || !float.IsFinite(m.M23) || !float.IsFinite(m.M24) ||
            !float.IsFinite(m.M31) || !float.IsFinite(m.M32) || !float.IsFinite(m.M33) || !float.IsFinite(m.M34) ||
            !float.IsFinite(m.M41) || !float.IsFinite(m.M42) || !float.IsFinite(m.M43) || !float.IsFinite(m.M44) ||
            m.M14 != 0f || m.M24 != 0f || m.M34 != 0f || m.M44 != 1f)
            throw new ArgumentException("Foliage transforms must be finite affine matrices.", "instances");

        double determinant = m.M11 * ((double)m.M22 * m.M33 - (double)m.M23 * m.M32) -
            m.M12 * ((double)m.M21 * m.M33 - (double)m.M23 * m.M31) +
            m.M13 * ((double)m.M21 * m.M32 - (double)m.M22 * m.M31);
        if (determinant == 0d)
            throw new ArgumentException("Foliage transforms must not be singular.", "instances");
    }

    static MeshBounds TransformBounds(in MeshBounds local, in Matrix4x4 transform, out float height)
    {
        if (!Finite(local.Min) || !Finite(local.Max) || local.Min.X > local.Max.X ||
            local.Min.Y > local.Max.Y || local.Min.Z > local.Max.Z)
            throw new ArgumentException("Foliage mesh bounds must be finite and ordered.", "boundsForMesh");

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        // Height fading contracts toward the authored root, staying within the original local bounds.
        float low = local.Min.Y, high = local.Max.Y;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = Vector3.Transform(new Vector3(
                (corner & 1) == 0 ? local.Min.X : local.Max.X,
                (corner & 2) == 0 ? low : high,
                (corner & 4) == 0 ? local.Min.Z : local.Max.Z), transform);
            if (!Finite(point))
                throw new ArgumentException("Foliage transforms must produce finite world bounds.", "instances");
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        double upLength = Math.Sqrt((double)transform.M21 * transform.M21 +
            (double)transform.M22 * transform.M22 + (double)transform.M23 * transform.M23);
        height = (float)(((double)local.Max.Y - local.Min.Y) * upLength);
        var result = new MeshBounds(min, max);
        if (!float.IsFinite(height) || !Finite(result.Center) || !float.IsFinite(result.Radius))
            throw new ArgumentException("Foliage transforms must produce finite world bounds and height.", "instances");
        return result;
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    readonly record struct PatchKey(double X, double Z, int MeshIndex, int MeshGeneration);
    readonly record struct Entry(PatchKey Key, FoliageInstance Instance, MeshBounds Bounds, float Height, int InputOrder);
}
