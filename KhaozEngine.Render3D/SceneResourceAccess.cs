using System;

namespace KhaozEngine.Render3D;

/// <summary>Resolves only resources published by its scene. Frame submission never loads or uploads.</summary>
public sealed class SceneResourceAccess(Scene3D scene) : ISceneResourceAccess
{
    readonly Scene3D _scene = scene ?? throw new ArgumentNullException(nameof(scene));
    public MeshHandle MeshOf(SceneResourceHandle handle) => _scene.ResourceMesh(handle);
    public void DrawResourceAnchored(SceneResourceHandle handle, in RigidInstanceDraw appearance, RenderAnchor anchor)
    {
        var draw = appearance with { Mesh = MeshOf(handle) };
        _scene.DrawAnchored(in draw, anchor);
    }
}
