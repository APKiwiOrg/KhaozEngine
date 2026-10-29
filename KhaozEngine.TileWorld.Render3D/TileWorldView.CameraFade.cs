using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    TileCameraFadeOptions? _cameraFade;
    TileCameraContainment? _cameraContainment;
    // Created with the view, before the prop clusters that read it, and never replaced.
    readonly TileCameraFadeState _cameraFades = new();
    HashSet<long> _cameraInside = new();
    HashSet<long> _cameraWasInside = new();

    /// <summary>
    /// The camera fade, off while null (the default). When set, <see cref="UpdateCameraFade"/> fades out the colour
    /// of every eligible object whose drawn triangles contain the eye, and every draw this view makes honours the
    /// fade: prop layers, ground props, roofs, the target outline and the silhouette. Shadows are never faded.
    /// Setting null clears every fade at once.
    /// </summary>
    public TileCameraFadeOptions? CameraFade
    {
        get => _cameraFade;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cameraFade = value;
            _cameraFades.Enabled = value is not null;
            if (value is not null) return;
            _cameraFades.Clear();
            _cameraInside.Clear();
            _cameraWasInside.Clear();
        }
    }

    /// <summary>How many objects are faded at all this frame.</summary>
    public int CameraFadedCount => _cameraFades.Count;

    /// <summary>Narrow-phase containment tests run so far, cumulative and never reset. Only objects whose box
    /// already holds the eye are tested against their triangles.</summary>
    public long CameraContainmentTests => _cameraContainment?.Tests ?? 0;

    /// <summary>One object's fade: 0 drawn in full, 1 colour withheld with its shadow kept.</summary>
    /// <param name="objectId">The object's document id.</param>
    public float CameraFadeOf(long objectId) => _cameraFades.TryGet(objectId, out float fade) ? fade : 0f;

    /// <summary>
    /// Advances the camera fade by <paramref name="dt"/> seconds for an eye at <paramref name="eye"/>, in absolute
    /// world space. Call it once per frame before <see cref="Draw(Vector3)"/>, after the camera has settled. Does
    /// nothing while <see cref="CameraFade"/> is null, and holds every fade still for a step that is not a finite
    /// positive number.
    /// </summary>
    public void UpdateCameraFade(Vector3 eye, float dt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cameraFade is not { } options) return;
        if (!(dt > 0f) || !float.IsFinite(dt)) return;

        _cameraContainment ??= new TileCameraContainment(_propLayerResolver);
        (_cameraWasInside, _cameraInside) = (_cameraInside, _cameraWasInside);
        _cameraContainment.Query(this, eye, options,
            new TileObjectCut(AppliedInteriorCut, CutArchetypeIds, _scene.InteriorCutFrame),
            _cameraWasInside, _cameraInside);
        _cameraFades.Advance(_cameraInside,
            options.FadeSeconds > 0f ? dt / options.FadeSeconds : float.PositiveInfinity);

        _cameraFades.ClearRegions();
        foreach (long id in _cameraFades.Ids())
        {
            // An object that left Gameplay residency or the document is not drawn, so its fade has nothing to hide
            // and must not survive to greet the object when it streams back in.
            if (_doc.FindObject(id) is not { } o || !IsGameplayObject(o))
            {
                _cameraFades.Remove(id);
                continue;
            }
            _cameraFades.MarkRegion(RegionCoord.Of(o.X, o.Z), o.Plane);
        }
    }

    /// <summary>One object's fade, for the draw paths. False while it is fully drawn.</summary>
    internal bool TryGetCameraFade(long objectId, out float fade) => _cameraFades.TryGet(objectId, out fade);

    /// <summary>Whether any faded object anchors in this region-plane, so a draw path can skip the per-placement
    /// split everywhere else.</summary>
    internal bool HasCameraFadeOn(RegionCoord region, int plane) => _cameraFades.AnyOn(region, plane);
}
