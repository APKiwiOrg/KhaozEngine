using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Owns only the mesh and texture handles created by one upload transaction.</summary>
internal sealed class AttachmentUpload : IAttachmentMeshLease
{
    readonly Scene3D _scene;
    readonly MeshHandle[] _handles;
    readonly Scene3D.SurfaceMaps[] _maps;
    readonly IReadOnlyList<MeshHandle> _view;
    bool _meshesUnloaded;
    bool _disposed;

    AttachmentUpload(Scene3D scene, int count)
    {
        _scene = scene;
        _handles = new MeshHandle[count];
        _maps = new Scene3D.SurfaceMaps[count];
        _view = Array.AsReadOnly(_handles);
    }

    internal static AttachmentUpload Create(Scene3D scene, IReadOnlyList<GltfMeshPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) throw new ArgumentException("An attachment upload needs material parts.", nameof(parts));
        var upload = new AttachmentUpload(scene, parts.Count);
        try
        {
            for (int i = 0; i < parts.Count; i++)
            {
                upload._maps[i] = scene.LoadSurfaceMaps(parts[i].Maps);
                upload._handles[i] = scene.LoadMesh(parts[i].Mesh, upload._maps[i]);
            }
            return upload;
        }
        catch
        {
            upload.Dispose();
            throw;
        }
    }

    public IReadOnlyList<MeshHandle> Handles
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _view; }
    }

    internal void UnloadMeshes()
    {
        if (_meshesUnloaded) return;
        _meshesUnloaded = true;
        foreach (MeshHandle handle in _handles) _scene.UnloadMesh(handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnloadMeshes();
        foreach (Scene3D.SurfaceMaps maps in _maps) _scene.UnloadSurfaceMaps(maps);
    }
}
