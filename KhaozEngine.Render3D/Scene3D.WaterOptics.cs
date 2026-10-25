using System;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly WaterSettings _waterOpticsScratch = new();

    /// <summary>Live and retired float32 texture and retained CPU payload bytes, plus cumulative successful
    /// optical uploads. Excludes opaque driver overhead and managed headers.</summary>
    public WaterOpticsStats WaterOpticsStats => _water.Optics.Stats;

    /// <summary>Upload an immutable field once and return its scene-owned handle. Recreating a scene requires
    /// explicitly loading retained CPU fields again. Old scene handles cannot be reused.</summary>
    public WaterOpticsHandle LoadWaterOpticsField(WaterOpticsField field) => _water.Optics.Load(field);

    /// <summary>Upload one immutable certified triangle atlas. Handles use the same scene ownership and
    /// retirement contract as bilinear fields. A triangle binding requires the field's exact size.</summary>
    public WaterOpticsHandle LoadWaterOpticsTriangles(WaterOpticsTriangleField field) => _water.Optics.LoadTriangles(field);

    /// <summary>Invalidate a field immediately and defer texture destruction through the scene's retirement queue.
    /// Default is a no-op. Foreign, stale and double-released handles throw <see cref="ArgumentException"/>.</summary>
    public void UnloadWaterOpticsField(WaterOpticsHandle handle) => _water.UnloadOpticsField(handle);

    /// <summary>Certified horizontal procedural swell padding for producer preflight against the effective look.
    /// FFT water is refused because it has no certified displacement envelope.</summary>
    public float GetWaterOpticsPadding(WaterLook? look = null) => WaterOpticsCoverage.Padding(
        look?.ResolveInto(_waterOpticsScratch, Post.Water) ?? Post.Water);

    /// <summary>The non-throwing form of <see cref="GetWaterOpticsPadding"/>. Returns false with
    /// <see cref="WaterOpticsSurfaceFailure.UncertifiedDisplacement"/> and zero padding when the effective look
    /// has no certified displacement envelope, such as FFT water. <paramref name="reason"/> is meaningful only
    /// when the method returns false.</summary>
    public bool TryGetWaterOpticsPadding(WaterLook? look, out float padding, out WaterOpticsSurfaceFailure reason)
    {
        bool certified = WaterOpticsCoverage.TryPadding(
            look?.ResolveInto(_waterOpticsScratch, Post.Water) ?? Post.Water, out padding);
        reason = certified ? default : WaterOpticsSurfaceFailure.UncertifiedDisplacement;
        return certified;
    }

    /// <summary>
    /// Queue one stylized water surface for this frame, over <paramref name="plane"/>'s still-water height and XZ footprint,
    /// drawn after the sky + ground decals: a Gerstner swell displacing the surface grid, a domain-warped non-tiling ripple
    /// normal field with a distance detail fade, per-channel depth absorption blended toward the analytically reflected sky
    /// by fresnel, a GGX sun glint, whitecap + shoreline foam, and a shore fade (knobs: <see cref="WaterSettings"/> on <see cref="Post"/>). Opt-in: no <see cref="DrawWater(in WaterPlane)"/> call this frame means the water pass
    /// (<see cref="Rendering.WaterRenderer"/>) never runs, so existing scenes stay byte-stable. Presentation
    /// only, cleared in <see cref="Begin"/>. Call once per frame per distinct body of water (a game with several
    /// separate lakes/ponds queues one <see cref="WaterPlane"/> each).
    /// </summary>
    public void DrawWater(in WaterPlane plane)
    {
        if (_framePrepared)
            throw new InvalidOperationException("Queue water before Scene3D.PrepareFrame().");
        if (plane.Optics.IsBound)
        {
            var payload = _water.Optics.Resolve(plane.Optics.Field).Payload;
            WaterOpticsCoverage.Validate(plane, plane.Look?.ResolveInto(_waterOpticsScratch, Post.Water) ?? Post.Water, payload);
        }
        _waterPlanes.Add(plane);
    }

    /// <summary>Count of water planes queued this frame. Internal: lets tests assert <see cref="Begin"/> clears
    /// the queue and <see cref="DrawWater(in WaterPlane)"/> enqueues.</summary>
    internal int WaterPlaneCount => _waterPlanes.Count;
}
