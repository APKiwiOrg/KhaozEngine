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
/// Planes above the observer are not tested for terrain. A surface hit within a millimetre of the pivot is the
/// ground the pivot stands on, which the pick reports for any boom because it counts both faces of the ground. That
/// touching start is skipped by picking once more from a centimetre above the pivot, so a boom rising away from
/// the ground keeps its length. A downward boom stops at the pivot when <c>0.01 / sin(angle)</c> is within the
/// radius, about 2.3 degrees at the default 0.25 m radius. Shallower downward booms can extend, but the eye stays
/// within a centimetre of the surface. A pivot on the ground is handled, but a pivot lifted clear of it with
/// <see cref="FollowCamera3D.PivotHeight"/> is the intended setup and costs one pick instead of two.</para>
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

    // A surface hit this near the pivot is a start contact, the ground the pivot stands on, not an obstruction.
    const float StartContact = 1e-3f;
    // How far above a touching pivot the second surface pick starts, clear of the ground under it.
    const float StartLift = 0.01f;

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

        if (SurfaceDistance(plane, origin, direction, length) is { } surface)
            reach = MathF.Min(reach, MathF.Max(0f, surface - radius));

        _radius = radius;
        reach = MathF.Min(reach, NearestObject(plane, origin, direction, length));
        if (plane + 1 < _view.Document.PlaneCount)
            reach = MathF.Min(reach, NearestObject(plane + 1, origin, direction, length));
        return reach;
    }

    // The distance to the first drawn surface along the boom, or null when it meets none. A start contact is picked
    // again from just above the pivot, not from a step along the boom: a step along a boom into the ground lands
    // under it, where a pick moving away from the ground meets nothing. From above, a boom rising away meets
    // nothing near, and a boom into the ground meets it StartLift / sin(angle) away, inside any usual radius for a
    // boom more than a few degrees into the ground.
    float? SurfaceDistance(int plane, Vector3 origin, Vector3 direction, float length)
    {
        if (_view.PickSurface(plane, origin, direction, length) is not { } hit) return null;
        if (hit.Distance > StartContact) return hit.Distance;
        return _view.PickSurface(plane, origin + new Vector3(0f, StartLift, 0f), direction, length)?.Distance;
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
