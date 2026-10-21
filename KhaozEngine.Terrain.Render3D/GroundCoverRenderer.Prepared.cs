using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

public static partial class GroundCoverRenderer
{
    /// <summary>Draw an admitted, published foliage resource. Focus and interactors are already anchor-local.</summary>
    public static int DrawPrepared(Scene3D scene, SceneResourceHandle foliage, Vector3 localFocus,
        in FoliageRenderSettings settings, RenderAnchor anchor, ReadOnlySpan<FoliageInteractor> interactors = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return scene.DrawPreparedFoliage(foliage, localFocus, settings, anchor, interactors);
    }
}
