using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    readonly List<PropPlacement> _cameraUnfaded = new();
    readonly List<PropPlacement> _cameraWithheld = new();
    readonly PropPlacement[] _cameraFadedOne = new PropPlacement[1];

    /// <summary>
    /// Draws one region-plane's ungrouped props while some of them are camera-faded: the untouched ones in one
    /// ordinary call, each partly faded one as dissolving colour plus a whole shadow, and each fully faded one as a
    /// shadow alone. The shadow is queued on its own because the dissolve threshold also discards in the shadow
    /// pass and would thin it. <paramref name="objectIds"/> runs parallel to <paramref name="placements"/>.
    /// </summary>
    /// <returns>The placements drawn in colour, partly faded ones included.</returns>
    int DrawCameraFaded(IReadOnlyList<PropPlacement> placements, IReadOnlyList<long> objectIds, Vector3 focus,
        ref int shadowOnly)
    {
        float radius = _options.PropDrawRadius;
        _cameraUnfaded.Clear();
        _cameraWithheld.Clear();
        int drawn = 0;
        for (int i = 0; i < placements.Count; i++)
        {
            if (i >= objectIds.Count || !TryGetCameraFade(objectIds[i], out float fade))
            {
                _cameraUnfaded.Add(placements[i]);
                continue;
            }
            if (fade >= 1f)
            {
                _cameraWithheld.Add(placements[i]);
                continue;
            }
            _cameraFadedOne[0] = placements[i];
            drawn += _scene.DrawColourOnlyProps(_cameraFadedOne, _propMeshes, focus, radius, fade);
            _scene.DrawShadowOnlyProps(_cameraFadedOne, _propMeshes, focus, radius);
        }
        if (_cameraUnfaded.Count > 0) drawn += _scene.DrawProps(_cameraUnfaded, _propMeshes, focus, radius);
        if (_cameraWithheld.Count > 0)
            shadowOnly += _scene.DrawShadowOnlyProps(_cameraWithheld, _propMeshes, focus, radius);
        return drawn;
    }
}
