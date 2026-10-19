using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>The released metre brush over a sparse native raster. Sample (x, z) sits at world (OriginX + x * CellSize,
/// OriginZ + z * CellSize) and rows advance positive world Z. A sample farther than the radius is skipped. A sample
/// within hardness times radius, or any reached sample when hardness is 1, takes full strength. The rest of the rim
/// blends linearly to zero weight at the radius. The blended value rounds half to even, then clamps to 0 to 255. Only
/// the chunks the disc reaches are read or written, and no whole-domain raster exists.</summary>
public static class MapFoliageBrush
{
    /// <summary>Density chunks one raster edit may reach.</summary>
    internal const int MaxChunks = 64;

    /// <summary>Raster addresses one raster edit may visit.</summary>
    internal const int MaxAddresses = 65_536;

    public static MapFoliageLayer Apply(MapFoliageLayer layer, MapPaintFoliage brush)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(brush);
        if (!string.Equals(layer.Id, brush.LayerId, StringComparison.Ordinal))
            throw new MapDocumentException($"Foliage paint names layer '{brush.LayerId}', not '{layer.Id}'.");
        if (!float.IsFinite(brush.WorldX) || !float.IsFinite(brush.WorldZ))
            throw new MapDocumentException("Foliage paint needs a finite world position.");
        if (!float.IsFinite(brush.RadiusMetres) || brush.RadiusMetres <= 0f)
            throw new MapDocumentException("Foliage paint radius must be finite and positive metres.");
        if (!float.IsFinite(brush.Hardness) || brush.Hardness < 0f || brush.Hardness > 1f)
            throw new MapDocumentException("Foliage paint hardness must be from 0 to 1.");
        RequireRaster(layer);
        float radius = brush.RadiusMetres, hardness = brush.Hardness, hardRadius = radius * hardness;
        float centreX = brush.WorldX, centreZ = brush.WorldZ;
        int target = brush.Target;
        if (Reach(layer.OriginX, layer.CellSize, layer.Width, centreX, radius) is not { } xs ||
            Reach(layer.OriginZ, layer.CellSize, layer.Depth, centreZ, radius) is not { } zs)
            return layer;
        return Rewrite(layer, xs.Lo, zs.Lo, xs.Hi, zs.Hi, (x, z, value) =>
        {
            float distance = Distance(x, z);
            if (distance > radius) return value;
            float strength = distance <= hardRadius || hardness == 1f ? 1f : (radius - distance) / (radius - hardRadius);
            return (byte)Math.Clamp((int)MathF.Round(value + ((target - value) * strength), MidpointRounding.ToEven), 0, byte.MaxValue);
        }, (x0, z0, x1, z1) => Distance(Nearest(layer.OriginX, layer.CellSize, x0, x1, centreX),
            Nearest(layer.OriginZ, layer.CellSize, z0, z1, centreZ)) <= radius);

        // The released expression order, so every reached byte matches bit for bit.
        float Distance(int x, int z)
        {
            float sampleX = layer.OriginX + (x * layer.CellSize);
            float sampleZ = layer.OriginZ + (z * layer.CellSize);
            return MathF.Sqrt(((sampleX - centreX) * (sampleX - centreX)) + ((sampleZ - centreZ) * (sampleZ - centreZ)));
        }
    }

    /// <summary>The sample in [a, b] with the smallest released single-axis offset from the centre. Offsets never decrease
    /// with the index, and each squared term grows with its offset, so the pair of per-axis nearest samples has the
    /// smallest released distance over the whole sample rectangle.</summary>
    static int Nearest(float origin, float cell, int a, int b, float centre)
    {
        if (Offset(a) >= 0f) return a;
        if (Offset(b) <= 0f) return b;
        while (b - a > 1)
        {
            int mid = a + (b - a) / 2;
            if (Offset(mid) < 0f) a = mid;
            else b = mid;
        }
        return -Offset(a) <= Offset(b) ? a : b;

        float Offset(int i) => origin + (i * cell) - centre;
    }

    internal static void RequireRaster(MapFoliageLayer layer)
    {
        if (!float.IsFinite(layer.OriginX) || !float.IsFinite(layer.OriginZ) || !float.IsFinite(layer.CellSize) ||
            layer.CellSize <= 0f || layer.Width < 1 || layer.Depth < 1)
            throw new MapDocumentException($"Foliage layer '{layer.Id}' has no valid density raster to edit.");
    }

    /// <summary>Refuses an edit over the inclusive sample box before any byte is read.</summary>
    internal static void RequireBudget(MapFoliageLayer layer, int loX, int loZ, int hiX, int hiZ)
    {
        long addresses = ((long)hiX - loX + 1) * ((long)hiZ - loZ + 1);
        if (addresses > MaxAddresses)
            throw new MapDocumentException($"Foliage layer '{layer.Id}': one raster edit visits {addresses} addresses, more than {MaxAddresses}.");
        long chunks = ((long)hiX / MapFoliageChunk.Size - loX / MapFoliageChunk.Size + 1) *
            ((long)hiZ / MapFoliageChunk.Size - loZ / MapFoliageChunk.Size + 1);
        if (chunks > MaxChunks)
            throw new MapDocumentException($"Foliage layer '{layer.Id}': one raster edit reaches {chunks} chunks, more than {MaxChunks}.");
    }

    /// <summary>Rewrites the inclusive sample box chunk by chunk. A chunk whose part of the box <paramref name="reaches"/>
    /// rejects is skipped before its residency is checked. An absent chunk reads zero and stays absent unless a byte
    /// changes. An explicit chunk keeps its identity when nothing changes, and an advertised nonresident chunk refuses.
    /// Returns the same layer when no byte changes.</summary>
    internal static MapFoliageLayer Rewrite(MapFoliageLayer layer, int loX, int loZ, int hiX, int hiZ, Func<int, int, byte, byte> sample,
        Func<int, int, int, int, bool>? reaches = null)
    {
        RequireBudget(layer, loX, loZ, hiX, hiZ);
        var resident = new Dictionary<MapFoliageChunkKey, MapFoliageChunk>();
        foreach (MapFoliageChunk chunk in layer.Chunks) resident.TryAdd(chunk.Key, chunk);
        var advertised = layer.ChunkDigests.Select(d => d.Key).ToHashSet();
        var replaced = new Dictionary<MapFoliageChunkKey, MapFoliageChunk>();
        const int size = MapFoliageChunk.Size;
        for (int cz = loZ / size; cz <= hiZ / size; cz++)
            for (int cx = loX / size; cx <= hiX / size; cx++)
            {
                var key = new MapFoliageChunkKey(cx, cz);
                int x0 = Math.Max(loX, cx * size), x1 = Math.Min(hiX, cx * size + size - 1);
                int z0 = Math.Max(loZ, cz * size), z1 = Math.Min(hiZ, cz * size + size - 1);
                if (reaches is not null && !reaches(x0, z0, x1, z1)) continue;
                byte[] bytes;
                if (resident.TryGetValue(key, out MapFoliageChunk? stored)) bytes = stored.CopyDensity();
                else if (advertised.Contains(key)) throw new MapDocumentException($"Foliage layer '{layer.Id}' chunk {key} is unavailable.");
                else bytes = new byte[MapFoliageChunk.ByteLength];
                bool changed = false;
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int index = (z - cz * size) * size + (x - cx * size);
                        byte next = sample(x, z, bytes[index]);
                        if (next == bytes[index]) continue;
                        bytes[index] = next;
                        changed = true;
                    }
                if (changed) replaced.Add(key, new MapFoliageChunk(key, bytes));
            }
        if (replaced.Count == 0) return layer;
        MapFoliageChunk[] chunks = layer.Chunks.Where(c => !replaced.ContainsKey(c.Key)).Concat(replaced.Values).ToArray();
        return MapFoliageLayer.FromStorage(layer, layer.ChunkDigests, chunks);
    }

    /// <summary>The inclusive sample range along one axis whose released single-axis distance reaches the disc. The full
    /// distance is never shorter, so no reached sample lies outside it. A double estimate is trimmed and widened with the
    /// released float arithmetic. Widening stops past the address budget, which then refuses.</summary>
    static (int Lo, int Hi)? Reach(float origin, float cell, int count, float centre, float radius)
    {
        double lo = Math.Floor(((double)centre - radius - origin) / cell) - 1;
        double hi = Math.Ceiling(((double)centre + radius - origin) / cell) + 1;
        if (!(hi >= 0) || !(lo <= count - 1)) return null;
        int a = (int)Math.Max(lo, 0), b = (int)Math.Min(hi, count - 1);
        while (a <= b && !Reaches(a)) a++;
        while (b >= a && !Reaches(b)) b--;
        if (a > b) return null;
        while (a > 0 && b - a < MaxAddresses && Reaches(a - 1)) a--;
        while (b < count - 1 && b - a < MaxAddresses && Reaches(b + 1)) b++;
        return (a, b);

        bool Reaches(int i)
        {
            float d = origin + (i * cell) - centre;
            return MathF.Sqrt(d * d) <= radius;
        }
    }
}
