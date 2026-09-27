using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld.Render3D;

/// <summary>
/// The <see cref="ICameraBoomProbe"/> for a tile world: how far a camera boom reaches from its pivot before it
/// meets the terrain, water and walk surfaces a <see cref="TileWorldView"/> draws, or an object the consumer's
/// filter says blocks the camera.
/// </summary>
/// <remarks>
/// <para>Terrain is <see cref="TileWorldView.PickSurface"/> on the observer's plane, stopped short by the radius.
/// Planes above the observer are not tested for terrain.</para>
/// <para>Objects are <see cref="TileWorldView.PickObjects"/> on the observer's plane and the plane above it, where
/// roofs stand, against every model box grown by the radius on each axis, which gives the boom a sphere's
/// clearance. Hits are walked nearest first and the first that passes survives. A box that already contains the
/// origin is skipped, so a subject pressed against a wall does not collapse its own boom. An archetype the filter
/// rejects never stops the boom, and neither does a roof <see cref="TileWorldView.IsRoofHidden"/> reports hidden,
/// because an invisible ceiling must never stop a camera.</para>
/// <para>The authored archetype decides, as in object picking, not a look override. Reuses one hit list and one
/// bounds delegate, so a call allocates nothing of its own. Not thread-safe, like the view it reads.</para>
/// </remarks>
public sealed class TileWorldCameraProbe : ICameraBoomProbe
{
    readonly TileWorldView _view;
    readonly TileObjectRaycast.BoundsSource _bounds;
    readonly TileObjectRaycast.BoundsSource _inflated;
    readonly Func<TileObjectArchetype, bool> _blocks;
    readonly List<TileObjectHit> _hits = new();
    float _radius;

    /// <summary>A probe over one view.</summary>
    /// <param name="view">The view whose drawn surfaces, observer and roof rule the boom is tested against.</param>
    /// <param name="bounds">The model box per archetype, normally <see cref="TileObjectBoundsCache.TryGetBounds"/>
    /// over the resolver the view draws with.</param>
    /// <param name="blocks">Which archetypes stop the camera. Tags are game content, so the engine names none.</param>
    public TileWorldCameraProbe(TileWorldView view, TileObjectRaycast.BoundsSource bounds,
                                Func<TileObjectArchetype, bool> blocks)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _inflated = Inflated;
    }

    /// <inheritdoc />
    public float Reach(Vector3 origin, Vector3 direction, float length, float radius)
    {
        float reach = length;
        int plane = _view.Observer.Plane;

        if (_view.PickSurface(plane, origin, direction, length) is { } surface)
            reach = MathF.Min(reach, MathF.Max(0f, surface.Distance - radius));

        _radius = radius;
        reach = MathF.Min(reach, NearestObject(plane, origin, direction, length));
        if (plane + 1 < _view.Document.PlaneCount)
            reach = MathF.Min(reach, NearestObject(plane + 1, origin, direction, length));
        return reach;
    }

    // The entry distance of the nearest grown box on one plane that stops the boom, or length when none does.
    float NearestObject(int plane, Vector3 origin, Vector3 direction, float length)
    {
        int count = _view.PickObjects(plane, origin, direction, length, _inflated, _hits);
        TileWorldDocument doc = _view.Document;
        TileWorldCatalogs catalogs = _view.Catalogs;
        for (int i = 0; i < count; i++)
        {
            TileObjectHit hit = _hits[i];
            if (hit.Distance <= 0f) continue;
            if (doc.FindObject(hit.ObjectId) is not { } o) continue;
            if (catalogs.Archetype(o.ArchetypeId) is not { } archetype || !_blocks(archetype)) continue;
            if (archetype.IsRoof && _view.IsRoofHidden(TileFootprint.Of(archetype, o.X, o.Z, o.Rotation), o.Plane))
                continue;
            return hit.Distance;
        }
        return length;
    }

    bool Inflated(TileObjectArchetype archetype, out Vector3 min, out Vector3 max)
    {
        if (!_bounds(archetype, out min, out max)) return false;
        var grow = new Vector3(_radius);
        min -= grow;
        max += grow;
        return true;
    }
}
