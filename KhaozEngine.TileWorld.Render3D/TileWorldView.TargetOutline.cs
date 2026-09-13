using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    long _outlinedObject;
    Color _outlineColor;
    float _outlineWidthPixels;

    /// <summary>The default selected-object outline width in physical framebuffer pixels.</summary>
    public const float DefaultOutlineWidthPixels = 1.25f;

    /// <summary>Outlines one authored object's projected union every frame until cleared.</summary>
    public void SetOutlinedObject(long objectId, Color color,
        float widthPixels = DefaultOutlineWidthPixels)
    {
        if (_outlinedObject != objectId) _propClusters.ClearOutlineSelection();
        _outlinedObject = objectId;
        _outlineColor = color;
        _outlineWidthPixels = widthPixels;
    }

    /// <summary>Stops drawing the selected object's pixel-width outline.</summary>
    public void ClearOutlinedObject()
    {
        _outlinedObject = 0L;
        _propClusters.ClearOutlineSelection();
    }

    void DrawOutlinedObject(Vector3 focus)
    {
        if (_doc.FindObject(_outlinedObject) is not { } o) return;
        RegionCoord region = RegionCoord.Of(o.X, o.Z);
        if (!_loaded.TryGetValue(region, out RegionHandles? handles)
            || handles.Residency != TileRegionResidencyState.Gameplay) return;
        string archetypeId = ArchetypeFor(o);
        if (_catalogs.Archetype(archetypeId) is not { } archetype) return;
        if (archetype.IsRoof && IsRoofHidden(TileFootprint.Of(archetype, o.X, o.Z, o.Rotation), o.Plane)) return;
        if (_propClusters.DrawOutline(handles.Props[o.Plane], o.Id, focus, _outlineColor,
            _outlineWidthPixels)) return;
        if (!_propMeshes.TryGetValue(archetypeId, out IReadOnlyList<MeshHandle>? parts)) return;
        Vector3 at = TileObjectProps.AnchorPosition(_doc, archetype, o);
        float dx = at.X - focus.X;
        float dz = at.Z - focus.Z;
        if (dx * dx + dz * dz > _options.PropDrawRadius * _options.PropDrawRadius) return;
        Matrix4x4 world = Matrix4x4.CreateRotationY(TileObjectProps.YawRadians(archetype, o.Rotation))
            * Matrix4x4.CreateTranslation(at);
        MeshOutlineGroup group = _scene.BeginMeshOutline(_outlineColor, _outlineWidthPixels);
        for (int i = 0; i < parts.Count; i++)
            _scene.DrawMeshOutline(group, parts[i], world);
    }
}
