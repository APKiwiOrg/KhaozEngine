using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>
/// The point light queue's SHADOW half: the overload that carries a <see cref="LightShadow"/> and the accessor
/// the shadow pass and the tests read it back through. Split out of <c>Scene3D.cs</c> rather than added to it,
/// because the light queue there is one member of a long frame-state list and this is a feature with its own
/// settings, its own atlas and its own pass.
/// </summary>
public sealed partial class Scene3D
{
    /// <summary>
    /// Queue a dynamic point light that also REQUESTS an omnidirectional shadow map, so it stops at walls
    /// instead of pooling through them. Identical to
    /// <see cref="AddLight(Vector3,Color,float,float)"/> in every other respect: same falloff, same budget, same
    /// per-frame clear.
    /// </summary>
    /// <remarks>
    /// <paramref name="shadow"/> is a REQUEST. <see cref="LightShadow.None"/> is exactly the four-argument
    /// overload and renders byte-identically to a scene with no point shadows at all.
    /// <see cref="LightShadow.Static"/> takes a cached map behind the caller's own key, for a placed light whose
    /// surroundings do not move, and <see cref="LightShadow.Dynamic"/> takes one rebuilt every frame, for a light
    /// that does. How many requests one frame can honour is
    /// <see cref="ShadowSettings.PointShadows"/>: past the budget a light is drawn unshadowed rather than
    /// dropped, nearest to the eye winning. Presentation only, exactly like the light itself.
    /// </remarks>
    public void AddLight(Vector3 worldPos, Color color, float radius, float intensity, LightShadow shadow)
    {
        Vector4 c = color;
        _lights.Add(new ModelRenderer.PointLightData
        {
            PosRadius = new Vector4(worldPos, radius),
            ColorIntensity = new Vector4(c.X, c.Y, c.Z, intensity),
            Shadow = shadow,
        });
    }

    /// <summary>The shadow request queued with light <paramref name="index"/> this frame, or
    /// <see cref="LightShadow.None"/> when there is no such light. Internal: the point shadow pass reads it to
    /// pick which lights get a slot, and the tests read it to prove the four-argument overload still queues an
    /// unshadowed light.</summary>
    internal LightShadow LightShadowAt(int index)
        => (uint)index < (uint)_lights.Count ? _lights[index].Shadow : LightShadow.None;
}
