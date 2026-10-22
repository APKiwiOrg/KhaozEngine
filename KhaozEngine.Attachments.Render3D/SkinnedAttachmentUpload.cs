using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Owns only the skinned mesh and texture handles created by one upload transaction.</summary>
internal sealed class SkinnedAttachmentUpload : ISkinnedAttachmentMeshLease, IAttachmentUpload
{
    readonly Scene3D _scene;
    readonly SkinnedMeshHandle[] _handles;
    readonly Scene3D.SurfaceMaps[] _maps;
    readonly IReadOnlyList<SkinnedMeshHandle> _view;
    bool _meshesUnloaded;
    bool _disposed;

    SkinnedAttachmentUpload(Scene3D scene, int count)
    {
        _scene = scene;
        _handles = new SkinnedMeshHandle[count];
        _maps = new Scene3D.SurfaceMaps[count];
        _view = Array.AsReadOnly(_handles);
    }

    internal static SkinnedAttachmentUpload Create(Scene3D scene, IReadOnlyList<GltfSkinnedMeshPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) throw new ArgumentException("An attachment upload needs material parts.", nameof(parts));
        SkinningProfile profile = parts[0].Mesh.Profile;
        for (int i = 1; i < parts.Count; i++)
            if (parts[i].Mesh.Profile != profile)
                throw new ArgumentException($"Attachment material part {i} profile must match the source skin profile " +
                    $"{profile.Method} version {profile.AlgorithmVersion} before upload.", nameof(parts));
        var upload = new SkinnedAttachmentUpload(scene, parts.Count);
        try
        {
            for (int i = 0; i < parts.Count; i++)
            {
                upload._maps[i] = scene.LoadSurfaceMaps(parts[i].Maps);
                upload._handles[i] = scene.LoadSkinnedMesh(parts[i].Mesh, upload._maps[i]);
            }
            return upload;
        }
        catch
        {
            upload.Dispose();
            throw;
        }
    }

    public IReadOnlyList<SkinnedMeshHandle> Handles
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _view; }
    }

    public void UnloadMeshes()
    {
        if (_meshesUnloaded) return;
        _meshesUnloaded = true;
        foreach (SkinnedMeshHandle handle in _handles) _scene.UnloadSkinnedMesh(handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnloadMeshes();
        foreach (Scene3D.SurfaceMaps maps in _maps) _scene.UnloadSurfaceMaps(maps);
    }
}
