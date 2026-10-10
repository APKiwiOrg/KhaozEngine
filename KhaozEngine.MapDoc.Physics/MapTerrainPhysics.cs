using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>How terrain faces are grouped into physics meshes. <see cref="MaxTrianglesPerChunk"/> is valid from 64 to
/// 1,024.</summary>
public sealed record MapTerrainChunkPolicy(int MaxTrianglesPerChunk = 1024);

/// <summary>One bounded one-sided triangle mesh of compiled terrain faces, installed at <see cref="Anchor"/>.</summary>
public sealed class MapTerrainChunk
{
    /// <summary>The stable chunk id. Residency and provenance key off it, so the grammar is fixed:
    /// <list type="bullet">
    /// <item><c>surfaceId/SlotX,SlotZ/minCellX,minCellZ,sizeCells</c>, a patch block of slot-local cells.</item>
    /// <item><c>surfaceId/SlotX,SlotZ/minCellX,minCellZ,1/ParentTriangle.Child</c>, a slot cell split by face to meet
    /// the anchor extent, named by its first face.</item>
    /// <item><c>stripId/firstPrimitive/Side</c>, a contiguous primitive range of one wall strip side.</item>
    /// <item><c>stripId/Primitive.ParentTriangle/Side</c>, one strip segment split by face to meet the anchor extent,
    /// named by its first face.</item>
    /// </list></summary>
    public string ChunkId { get; }

    /// <summary>Whole metres, <c>Floor(centre + 1/2)</c> of the chunk's exact bounds on each axis.</summary>
    public MapSubmissionAnchor Anchor { get; }

    /// <summary>Vertices are offsets from <see cref="Anchor"/>, each rounded once from its exact value. Triangle i is
    /// emitted as R2's (A, C, B), so the backend front <c>cross(C - A, B - A)</c> follows the compiled normal.</summary>
    public TriangleMeshShape Shape { get; }

    /// <summary>The compiled face of each triangle, in face key order.</summary>
    public IReadOnlyList<MapFaceKey> TriangleOwners { get; }

    /// <summary>The compiled role of each triangle.</summary>
    public IReadOnlyList<MapFaceRole> TriangleRoles { get; }

    /// <summary>The lowercase hex SHA-256 over the id, anchor, vertex float bits, indices, owners and roles.</summary>
    public string Digest { get; }

    internal MapTerrainChunk(string chunkId, MapSubmissionAnchor anchor, TriangleMeshShape shape,
        MapFaceKey[] owners, MapFaceRole[] roles, string digest)
    {
        ChunkId = chunkId;
        Anchor = anchor;
        Shape = shape;
        TriangleOwners = Array.AsReadOnly(owners);
        TriangleRoles = Array.AsReadOnly(roles);
        Digest = digest;
    }
}

/// <summary>The physics chunks of one complete surface view.</summary>
public sealed class MapTerrainChunkSet
{
    /// <summary>Every chunk, ordered by ordinal <see cref="MapTerrainChunk.ChunkId"/>.</summary>
    public IReadOnlyList<MapTerrainChunk> Chunks { get; }

    /// <summary>The read witness of the view the chunks were compiled from.</summary>
    public MapReadWitness Witness { get; }

    /// <summary>Legacy fallback cells R2 compiles to no faces, so they carry no triangles here either.</summary>
    public int LegacyFallbackCellsSkipped { get; }

    internal MapTerrainChunkSet(IReadOnlyList<MapTerrainChunk> chunks, MapReadWitness witness, int skipped)
    {
        Chunks = chunks;
        Witness = witness;
        LegacyFallbackCellsSkipped = skipped;
    }
}

/// <summary>Compiles R2's support floors, ceilings and wall strips into bounded physics meshes.</summary>
public static class MapTerrainPhysics
{
    // The accepted triangle cap range.
    internal const int MinTrianglesPerChunk = 64, MaxTrianglesPerChunk = 1024;

    // The capsule feature query refuses installed vertices beyond this many metres on any local axis.
    internal const int MaxAnchorOffsetMetres = 64;

    /// <summary>Chunks every present support floor and ceiling patch of a complete view, quadrant by quadrant within
    /// each 64 by 64 slot block, and every wall strip side by contiguous primitive range, until each chunk holds at
    /// most the cap and every exact vertex lies within 64 m of its anchor on every axis. Faces are
    /// R2's, never retriangulated. A slot cell is split by face only to meet the extent, and a single face that fits
    /// no anchor keeps a chunk of its own. Throws <see cref="MapDocumentException"/> for a view that is not complete,
    /// a slot cell over the cap ("physics chunk capacity"), a wall strip chain that does not resolve ("unresolved
    /// chain"), and geometry that is not representable.</summary>
    public static MapTerrainChunkSet Compile(MapScopedSurfaces view, MapTerrainChunkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxTrianglesPerChunk is < MinTrianglesPerChunk or > MaxTrianglesPerChunk)
            throw new ArgumentOutOfRangeException(nameof(policy),
                $"MaxTrianglesPerChunk must be {MinTrianglesPerChunk} to {MaxTrianglesPerChunk}.");
        if (view.Status != MapAcquireStatus.Complete)
            throw new MapDocumentException($"terrain physics needs a complete surface view, not {view.Status}" +
                (view.Detail is null ? "" : ": " + view.Detail));
        var surfaces = view.Surfaces.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var chunker = new Chunker(policy.MaxTrianglesPerChunk);
        int skipped = 0;
        string owner = "the view";
        try
        {
            foreach (MapPatchKey key in view.Witness.Present.Select(p => p.Key).Order())
            {
                if (!surfaces.TryGetValue(key.SurfaceId, out MapSurfaceRef? surface))
                    throw new MapDocumentException($"terrain physics patch '{key}' has no declared surface");
                if (surface.Role != MapSurfaceRole.PaintOverride)
                {
                    owner = $"patch '{key}'";
                    MapSurfacePatch patch = view.Patch(key).Patch ??
                        throw new MapDocumentException($"terrain physics patch '{key}' is unavailable");
                    MapCompiledPatch compiled = MapSurfaceCompiler.Compile(surface, patch);
                    skipped += compiled.LegacyFallbackCells.Count;
                    chunker.AddPatch(key, compiled.ExactVertices, compiled.Faces);
                }
                foreach (MapWallStrip strip in view.RecordsIn(key).OfType<MapWallStrip>())
                {
                    owner = $"wall strip '{strip.Id}'";
                    MapCompiledStrip compiled = MapWallStripCompiler.Compile(strip,
                        Chain(view, strip, strip.LowerChain), Chain(view, strip, strip.UpperChain));
                    chunker.AddStrip(strip.Id, compiled.ExactVertices, compiled.Faces);
                }
            }
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException($"terrain physics geometry is not representable in {owner}", error);
        }
        return new(chunker.Finish(), view.ReadWitness, skipped);
    }

    static MapChainResolution Chain(MapScopedSurfaces view, MapWallStrip strip, MapRecordRef reference)
    {
        if (!view.TryRecord(reference, out MapTopologyRecord? record, out MapPatchStatus status) ||
            record is not MapBoundaryChain chain)
            throw new MapDocumentException(
                $"unresolved chain '{reference.Id}' for wall strip '{strip.Id}': no boundary chain in '{reference.Anchor}' ({status})");
        MapChainResolution resolved = MapBoundaryGeometry.ResolveChain(chain, view);
        if (resolved.Status != MapResolveStatus.Resolved)
            throw new MapDocumentException(
                $"unresolved chain '{chain.Id}' for wall strip '{strip.Id}' ({resolved.Status}): {resolved.Detail}");
        return resolved;
    }

    /// <summary>Recursive splitting under the triangle cap and the anchor extent.</summary>
    sealed class Chunker(int cap)
    {
        static readonly MapExactValue Half = new(1, 2), Limit = new(MaxAnchorOffsetMetres, 1);
        readonly List<MapTerrainChunk> _chunks = new();

        internal void AddPatch(MapPatchKey key, IReadOnlyList<MapExactPoint> exact, IReadOnlyList<MapCompiledFace> faces)
        {
            string prefix = FormattableString.Invariant($"{key.SurfaceId}/{key.SlotX},{key.SlotZ}/");
            Block(key, prefix, exact, faces.OrderBy(f => f.Key).ToArray(), 0, 0, MapPatchKey.SlotCells);
        }

        void Block(MapPatchKey key, string prefix, IReadOnlyList<MapExactPoint> exact, MapCompiledFace[] faces,
            int minX, int minZ, int size)
        {
            if (faces.Length == 0) return;
            string id = prefix + FormattableString.Invariant($"{minX},{minZ},{size}");
            MapSubmissionAnchor anchor = Anchor(exact, faces, out bool fits);
            if (faces.Length <= cap && fits)
            {
                Emit(id, anchor, exact, faces);
                return;
            }
            if (size == 1)
            {
                if (faces.Length > cap)
                    throw new MapDocumentException(FormattableString.Invariant(
                        $"physics chunk capacity: patch '{key}' slot cell {minZ * MapPatchKey.SlotCells + minX} ({minX}, {minZ}) compiles to {faces.Length} faces, over the cap of {cap}"));
                SplitByFace(exact, faces, k => id + FormattableString.Invariant($"/{k.ParentTriangle}.{k.Child}"));
                return;
            }
            int half = size / 2;
            for (int qz = 0; qz < 2; qz++)
                for (int qx = 0; qx < 2; qx++)
                {
                    int x0 = minX + qx * half, z0 = minZ + qz * half;
                    Block(key, prefix, exact, faces.Where(f => Inside(f.Key.Primitive, x0, z0, half)).ToArray(), x0, z0, half);
                }
        }

        static bool Inside(int slotCell, int minX, int minZ, int size)
        {
            int x = slotCell % MapPatchKey.SlotCells, z = slotCell / MapPatchKey.SlotCells;
            return x >= minX && x < minX + size && z >= minZ && z < minZ + size;
        }

        internal void AddStrip(string stripId, IReadOnlyList<MapExactPoint> exact, IReadOnlyList<MapCompiledFace> faces)
        {
            foreach (MapSide side in new[] { MapSide.Front, MapSide.Back })
            {
                MapCompiledFace[] sided = faces.Where(f => f.Key.Side == side).OrderBy(f => f.Key).ToArray();
                if (sided.Length != 0) Range(stripId, side, exact, sided, 0, sided[^1].Key.Primitive + 1);
            }
        }

        void Range(string stripId, MapSide side, IReadOnlyList<MapExactPoint> exact, MapCompiledFace[] faces, int lo, int hi)
        {
            if (faces.Length == 0) return;
            MapSubmissionAnchor anchor = Anchor(exact, faces, out bool fits);
            if (faces.Length <= cap && fits)
            {
                Emit(FormattableString.Invariant($"{stripId}/{faces[0].Key.Primitive}/{side}"), anchor, exact, faces);
                return;
            }
            if (hi - lo > 1)
            {
                int mid = lo + (hi - lo) / 2;
                Range(stripId, side, exact, faces.Where(f => f.Key.Primitive < mid).ToArray(), lo, mid);
                Range(stripId, side, exact, faces.Where(f => f.Key.Primitive >= mid).ToArray(), mid, hi);
                return;
            }
            // One ruled quad holds at most two faces, always under the cap, so only the extent splits it.
            SplitByFace(exact, faces, k => FormattableString.Invariant($"{stripId}/{k.Primitive}.{k.ParentTriangle}/{side}"));
        }

        /// <summary>Halves a face run in key order until each half fits its anchor or is a single face.</summary>
        void SplitByFace(IReadOnlyList<MapExactPoint> exact, MapCompiledFace[] faces, Func<MapFaceKey, string> id)
        {
            MapSubmissionAnchor anchor = Anchor(exact, faces, out bool fits);
            if (fits || faces.Length == 1)
            {
                Emit(id(faces[0].Key), anchor, exact, faces);
                return;
            }
            int mid = faces.Length / 2;
            SplitByFace(exact, faces[..mid], id);
            SplitByFace(exact, faces[mid..], id);
        }

        static MapSubmissionAnchor Anchor(IReadOnlyList<MapExactPoint> exact, MapCompiledFace[] faces, out bool fits)
        {
            MapExactPoint first = exact[faces[0].A];
            MapExactValue minX = first.X, maxX = first.X, minY = first.Y, maxY = first.Y, minZ = first.Z, maxZ = first.Z;
            foreach (MapCompiledFace face in faces)
                foreach (int index in new[] { face.A, face.B, face.C })
                {
                    MapExactPoint p = exact[index];
                    if (p.X.CompareTo(minX) < 0) minX = p.X;
                    if (p.X.CompareTo(maxX) > 0) maxX = p.X;
                    if (p.Y.CompareTo(minY) < 0) minY = p.Y;
                    if (p.Y.CompareTo(maxY) > 0) maxY = p.Y;
                    if (p.Z.CompareTo(minZ) < 0) minZ = p.Z;
                    if (p.Z.CompareTo(maxZ) > 0) maxZ = p.Z;
                }
            long x = Centre(minX, maxX), y = Centre(minY, maxY), z = Centre(minZ, maxZ);
            fits = Within(minX, maxX, x) && Within(minY, maxY, y) && Within(minZ, maxZ, z);
            return new(x, y, z);
        }

        static long Centre(MapExactValue min, MapExactValue max) => min.Add(max).Multiply(Half).Add(Half).Floor();

        static bool Within(MapExactValue min, MapExactValue max, long anchor)
        {
            var at = new MapExactValue(anchor, 1);
            return max.Subtract(at).CompareTo(Limit) <= 0 && at.Subtract(min).CompareTo(Limit) <= 0;
        }

        void Emit(string id, MapSubmissionAnchor anchor, IReadOnlyList<MapExactPoint> exact, MapCompiledFace[] faces)
        {
            var slots = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var indices = new int[faces.Length * 3];
            var owners = new MapFaceKey[faces.Length];
            var roles = new MapFaceRole[faces.Length];
            var origin = new MapExactPoint(new(anchor.X, 1), new(anchor.Y, 1), new(anchor.Z, 1));
            for (int i = 0; i < faces.Length; i++)
            {
                MapCompiledFace face = faces[i];
                // R2's normal is cross(B - A, C - A). The backend front is cross(C - A, B - A), so C and B swap.
                indices[3 * i] = Vertex(face.A);
                indices[3 * i + 1] = Vertex(face.C);
                indices[3 * i + 2] = Vertex(face.B);
                owners[i] = face.Key;
                roles[i] = face.Role;
            }
            Vector3[] offsets = vertices.ToArray();
            _chunks.Add(new(id, anchor, new TriangleMeshShape(offsets, indices), owners, roles,
                Digest(id, anchor, offsets, indices, owners, roles)));

            int Vertex(int source)
            {
                if (slots.TryGetValue(source, out int slot)) return slot;
                MapExactPoint p = exact[source];
                slots.Add(source, vertices.Count);
                vertices.Add(new(p.X.Subtract(origin.X).ToSingle(), p.Y.Subtract(origin.Y).ToSingle(),
                    p.Z.Subtract(origin.Z).ToSingle()));
                return vertices.Count - 1;
            }
        }

        internal IReadOnlyList<MapTerrainChunk> Finish()
        {
            MapTerrainChunk[] ordered = _chunks.OrderBy(c => c.ChunkId, StringComparer.Ordinal).ToArray();
            for (int i = 1; i < ordered.Length; i++)
                if (string.Equals(ordered[i - 1].ChunkId, ordered[i].ChunkId, StringComparison.Ordinal))
                    throw new MapDocumentException($"duplicate physics chunk id '{ordered[i].ChunkId}'");
            return Array.AsReadOnly(ordered);
        }
    }

    static string Digest(string id, MapSubmissionAnchor anchor, Vector3[] vertices, int[] indices, MapFaceKey[] owners,
        MapFaceRole[] roles)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[8];
        WriteString(hash, id);
        foreach (long value in new[] { anchor.X, anchor.Y, anchor.Z })
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            hash.AppendData(buffer);
        }
        WriteInt(hash, vertices.Length);
        foreach (Vector3 v in vertices)
        {
            WriteInt(hash, BitConverter.SingleToInt32Bits(v.X));
            WriteInt(hash, BitConverter.SingleToInt32Bits(v.Y));
            WriteInt(hash, BitConverter.SingleToInt32Bits(v.Z));
        }
        WriteInt(hash, indices.Length);
        foreach (int index in indices) WriteInt(hash, index);
        WriteInt(hash, owners.Length);
        for (int i = 0; i < owners.Length; i++)
        {
            MapFaceKey key = owners[i];
            WriteString(hash, key.OwnerId);
            WriteString(hash, key.Patch?.SurfaceId);
            foreach (long value in new[] { key.Patch?.SlotX ?? 0, key.Patch?.SlotZ ?? 0 })
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
                hash.AppendData(buffer);
            }
            WriteInt(hash, key.Primitive);
            WriteInt(hash, key.ParentTriangle);
            WriteInt(hash, key.Child);
            WriteInt(hash, (int)key.Side);
            WriteInt(hash, (int)roles[i]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static void WriteInt(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    // A length prefix keeps adjacent fields unambiguous. A null string writes length -1.
    static void WriteString(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            WriteInt(hash, -1);
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteInt(hash, bytes.Length);
        hash.AppendData(bytes);
    }
}
