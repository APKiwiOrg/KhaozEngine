using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>
/// The roof rule: which of a region-plane's roofs this frame draws, which it hides, and what happens to the ones
/// it hides.
/// <para>
/// A hidden roof is WITHHELD FROM THE EYE, not removed from the world. Until issue #974 it was removed: the
/// filtered placements never reached <see cref="ITileWorldScene"/> at all, so the key light's depth pass never saw
/// them either and the sun landed on the interior floor of the building the observer was standing in. They are now
/// queued through <see cref="ITileWorldScene.DrawShadowOnlyProps"/>, which records their depth and draws no colour,
/// so the room reads as indoors while the camera still sees into it.
/// </para>
/// </summary>
public sealed partial class TileWorldView
{
    // One region-plane's roofs split into what this frame draws and what it withholds. Refilled per region-plane
    // rather than allocated, and only ever handed to ITileWorldScene.DrawProps / DrawShadowOnlyProps, both of
    // which read during the call and do not retain, so one pair of lists serves the whole frame.
    readonly List<PropPlacement> _visibleRoofs = new();
    readonly List<long> _visibleRoofIds = new();
    readonly List<PropPlacement> _hiddenRoofs = new();

    /// <summary>The roof rule, and the whole of it. Under <see cref="RoofVisibility.Interior"/> a roof is hidden
    /// when the observer is indoors, the roof sits on a plane ABOVE the observer's own (so the storey they stand
    /// on keeps its own ceiling and only what is between them and the camera goes), and the roof's footprint
    /// touches the observer's interior, which is what keeps the rule to one building. The other two modes answer
    /// without looking at the world at all. Hidden means invisible rather than absent: a hidden roof still casts
    /// (issue #974).</summary>
    /// <param name="footprint">The world tile rect the roof covers, from <see cref="TileFootprint.Of"/>. An
    /// empty rect is never hidden by the interior rule.</param>
    /// <param name="plane">The plane the roof stands on.</param>
    public bool IsRoofHidden(TileRect footprint, int plane)
    {
        EnsureInterior();
        return RoofRule.IsHidden(footprint, plane);
    }

    // Queues one region-plane's roofs: the visible ones through the ordinary prop path, the hidden ones as
    // shadow-only casters at the same focus and the same draw radius, so a roof outside the radius is queued in
    // neither. Returns the visible count and adds the shadow-only count to the frame's running total, because
    // LastDrawnProps stays the VISIBLE total and a shadow the player cannot see is not a prop they were shown.
    int DrawRoofs(TileRegionProps props, int plane, Vector3 focus, ref int shadowOnly)
    {
        (IReadOnlyList<PropPlacement> visible, IReadOnlyList<long> visibleIds, IReadOnlyList<PropPlacement> hidden) =
            SplitRoofs(props, plane);
        int drawn = visible.Count == 0 ? 0
            : HasCameraFadeOn(props.Region, plane) ? DrawCameraFaded(visible, visibleIds, focus, ref shadowOnly)
            : _scene.DrawProps(visible, _propMeshes, focus, _options.PropDrawRadius);
        if (hidden.Count > 0)
            shadowOnly += _scene.DrawShadowOnlyProps(hidden, _propMeshes, focus, _options.PropDrawRadius);
        return drawn;
    }

    // One region-plane's roofs split in a single walk. Both ends of the split can be the region's OWN list with
    // nothing copied: nothing on the plane can be hidden (every outdoor frame, and every plane at or below the
    // observer) hands back the whole list as visible, and the roofs-off mode hands the whole list back as hidden.
    // Only the per-building rule refills the two scratch lists.
    (IReadOnlyList<PropPlacement> Visible, IReadOnlyList<long> VisibleIds, IReadOnlyList<PropPlacement> Hidden)
        SplitRoofs(TileRegionProps props, int plane)
    {
        if (!RoofRule.AnyHiddenOn(plane)) return (props.Roofs, props.RoofObjectIds, Array.Empty<PropPlacement>());
        if (RoofMode != RoofVisibility.Interior)
            return (Array.Empty<PropPlacement>(), Array.Empty<long>(), props.Roofs);

        _visibleRoofs.Clear();
        _visibleRoofIds.Clear();
        _hiddenRoofs.Clear();
        IReadOnlyList<TileRect> footprints = props.RoofFootprints;
        for (int i = 0; i < props.Roofs.Count; i++)
        {
            // A roof the footprint list does not reach is one nothing placed, so it is not part of any interior
            // and stays visible. TileObjectProps.Build always fills the list, so this is the hand-built case.
            TileRect footprint = i < footprints.Count ? footprints[i] : default;
            if (RoofRule.IsHidden(footprint, plane)) _hiddenRoofs.Add(props.Roofs[i]);
            else
            {
                _visibleRoofs.Add(props.Roofs[i]);
                _visibleRoofIds.Add(i < props.RoofObjectIds.Count ? props.RoofObjectIds[i] : 0L);
            }
        }
        return (_visibleRoofs, _visibleRoofIds, _hiddenRoofs);
    }

    TileRoofVisibilityRule RoofRule => new(RoofMode, ObserverIndoors, _observer.Plane, _interior);
}
