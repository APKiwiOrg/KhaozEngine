using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>The <see cref="ITileMeshGeometry"/> that reads clearance meshes from glb files under a kit root. A mesh
/// reference maps to a path exactly as <see cref="GltfMeshResolver.PathFor(string)"/> maps it for drawing, and the
/// geometry is <see cref="GltfLoader.Load(string)"/>'s, so node transforms are already baked and the mesh sits in the
/// archetype's local metres the way it draws.
///
/// <para>Nothing here throws over bad content or a bad reference. A blank or path-rejected reference, a missing
/// file, a loader throw and a load that yields no valid <see cref="ClearanceMesh"/> each return false with a reason
/// naming the reference, and the full path once there is one.</para>
///
/// <para>Each load is cached per reference together with its file stamp, the file's last write time and length.
/// Every ask re-stats the file and reloads when the stamp differs, so a glb exported or re-exported after the first
/// ask is seen at the next one while an unchanged glb is parsed once. A missing file is probed again on every ask
/// rather than remembered. Only a reference that names no usable path stays cached as a failure, because that
/// depends on the reference alone. Only the named file is stamped, so a .gltf whose external buffers or images
/// change without the .gltf itself changing keeps its old load. The cache is a plain dictionary, so load on one
/// thread.</para></summary>
public sealed class GltfTileMeshGeometry : ITileMeshGeometry
{
    readonly GltfMeshResolver _paths;
    readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    // Path is null for a reference that names no usable path, and then the error stands for good. Otherwise the
    // answer holds while the file at Path still carries Stamp.
    readonly record struct Entry(string? Path, (DateTime WriteTimeUtc, long Length) Stamp, ClearanceMesh? Mesh, string? Error);

    /// <summary>A mesh source that reads every mesh reference relative to <paramref name="rootDirectory"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="rootDirectory"/> is null.</exception>
    public GltfTileMeshGeometry(string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        _paths = new GltfMeshResolver(rootDirectory);
    }

    /// <inheritdoc />
    public bool TryLoad(string meshRef, [NotNullWhen(true)] out ClearanceMesh? mesh, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(meshRef))
        {
            mesh = null;
            error = "the mesh reference is blank";
            return false;
        }

        Entry entry = Current(meshRef);
        mesh = entry.Mesh;
        if (mesh is not null)
        {
            error = null;
            return true;
        }

        error = entry.Error ?? $"mesh '{meshRef}' did not load";
        return false;
    }

    Entry Current(string meshRef)
    {
        bool known = _cache.TryGetValue(meshRef, out Entry entry);
        if (known && entry.Path is null) return entry;

        string path;
        if (known)
        {
            path = entry.Path!;
        }
        else
        {
            try
            {
                path = _paths.PathFor(meshRef);
            }
            catch (Exception ex)
            {
                return _cache[meshRef] = new Entry(null, default,
                    null, $"mesh '{meshRef}' does not name a usable path: {ex.Message}");
            }
        }

        (DateTime, long)? stamp;
        try
        {
            var file = new FileInfo(path);
            stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
        }
        catch (Exception ex)
        {
            // A stat that fails is reported like a load that fails, and asked again next time.
            _cache.Remove(meshRef);
            return new Entry(path, default, null, $"mesh '{meshRef}' at '{path}' could not be read: {ex.Message}");
        }

        if (stamp is not { } current)
        {
            // Not cached, so the file is probed again at the next ask and an export in between is seen.
            _cache.Remove(meshRef);
            return new Entry(path, default, null, $"mesh '{meshRef}' is missing at '{path}'");
        }

        if (known && entry.Stamp == current) return entry;
        return _cache[meshRef] = Load(meshRef, path, current);
    }

    static Entry Load(string meshRef, string path, (DateTime WriteTimeUtc, long Length) stamp)
    {
        try
        {
            GltfMesh loaded = GltfLoader.Load(path);
            var positions = new Vector3[loaded.Vertices.Length];
            for (int i = 0; i < positions.Length; i++) positions[i] = loaded.Vertices[i].Position;
            var indices = new int[loaded.Indices32.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                uint index = loaded.Indices32[i];
                indices[i] = index > int.MaxValue ? -1 : (int)index;
            }

            return new Entry(path, stamp, new ClearanceMesh(positions, indices), null);
        }
        catch (Exception ex)
        {
            // Deliberately broad: a corrupt or unsupported glb surfaces as anything from a format exception to an
            // IO one, and a glb that loads into no valid clearance mesh throws ArgumentException. Each is one
            // reported mesh, never a failed check. Cached under the stamp it was read at, so a re-export retries.
            return new Entry(path, stamp, null, $"mesh '{meshRef}' at '{path}' could not be read: {ex.Message}");
        }
    }
}
