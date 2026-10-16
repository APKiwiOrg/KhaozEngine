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
/// naming the reference, and the full path once there is one. Each answer is cached per reference, failures
/// included, so a glb written after the first ask is not picked up. The cache is a plain dictionary, so load on
/// one thread.</para></summary>
public sealed class GltfTileMeshGeometry : ITileMeshGeometry
{
    readonly GltfMeshResolver _paths;
    readonly Dictionary<string, (ClearanceMesh? Mesh, string? Error)> _cache = new(StringComparer.Ordinal);

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

        if (!_cache.TryGetValue(meshRef, out (ClearanceMesh? Mesh, string? Error) entry))
            _cache[meshRef] = entry = Load(meshRef);

        mesh = entry.Mesh;
        if (mesh is not null)
        {
            error = null;
            return true;
        }

        error = entry.Error ?? $"mesh '{meshRef}' did not load";
        return false;
    }

    (ClearanceMesh? Mesh, string? Error) Load(string meshRef)
    {
        string path;
        try
        {
            path = _paths.PathFor(meshRef);
        }
        catch (Exception ex)
        {
            return (null, $"mesh '{meshRef}' does not name a usable path: {ex.Message}");
        }

        try
        {
            if (!File.Exists(path)) return (null, $"mesh '{meshRef}' is missing at '{path}'");
            GltfMesh loaded = GltfLoader.Load(path);
            var positions = new Vector3[loaded.Vertices.Length];
            for (int i = 0; i < positions.Length; i++) positions[i] = loaded.Vertices[i].Position;
            var indices = new int[loaded.Indices32.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                uint index = loaded.Indices32[i];
                indices[i] = index > int.MaxValue ? -1 : (int)index;
            }

            return (new ClearanceMesh(positions, indices), null);
        }
        catch (Exception ex)
        {
            // Deliberately broad: a corrupt or unsupported glb surfaces as anything from a format exception to an
            // IO one, and a glb that loads into no valid clearance mesh throws ArgumentException. Each is one
            // reported mesh, never a failed check.
            return (null, $"mesh '{meshRef}' at '{path}' could not be read: {ex.Message}");
        }
    }
}
