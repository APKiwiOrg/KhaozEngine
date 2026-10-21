using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

public static partial class PropRenderer
{
    /// <summary>Submit all selected native parts at their precise anchors using caller-supplied N-tier distances.</summary>
    public static int DrawNative(Scene3D scene, IReadOnlyList<NativePropInstance> instances,
        IReadOnlyDictionary<string, IReadOnlyList<NativePropLod>> lods, Vector3 focus, float drawRadius,
        IReadOnlyList<float> lodDistances, float crossfadeWidth)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return EmitNative(instances, lods, focus, drawRadius, lodDistances, crossfadeWidth, scene,
            static (target, instance, draw) => target.DrawAnchored(draw, instance.Anchor));
    }

    /// <summary>Draw a frozen native layer through the same selection and coverage emitter as native recording.</summary>
    public static int DrawNative(Scene3D scene, PropLayer layer, Vector3 focus)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return EmitNative(layer, focus, scene, static (target, instance, draw) => target.DrawAnchored(draw, instance.Anchor));
    }

    internal static int EmitNative<T>(PropLayer layer, Vector3 focus, T state,
        Action<T, NativePropInstance, RigidInstanceDraw> emit, float dissolveFloor = 0, Func<RenderAnchor, Vector3>? localFocus = null, Func<T, RenderAnchor, Vector3>? resolveFocus = null)
    {
        var instances = layer.NativePlacements ?? throw new ArgumentException("A native placement layer is required.", nameof(layer));
        return EmitNative(instances, layer.NativeLods!, focus, layer.DrawRadius, layer.LodDistances,
            layer.LodCrossfadeWidth, state, emit, dissolveFloor, layer.CastsShadows, localFocus, resolveFocus);
    }

    /// <summary>The shared device and typed-recorder emission path. Focus is absolute binary32, instances retain
    /// exact anchors. The optional local focus avoids a rounded absolute focus for native frame consumers.</summary>
    internal static int EmitNative<T>(IReadOnlyList<NativePropInstance> instances,
        IReadOnlyDictionary<string, IReadOnlyList<NativePropLod>> lods, Vector3 focus, float drawRadius,
        IReadOnlyList<float> lodDistances, float crossfadeWidth, T state, Action<T, NativePropInstance, RigidInstanceDraw> emit,
        float dissolveFloor = 0, bool castsShadows = true, Func<RenderAnchor, Vector3>? localFocus = null, Func<T, RenderAnchor, Vector3>? resolveFocus = null)
    {
        ArgumentNullException.ThrowIfNull(instances); ArgumentNullException.ThrowIfNull(lods); ArgumentNullException.ThrowIfNull(lodDistances);
        int count = 0;
        for (int row = 0; row < instances.Count; row++)
        {
            var instance = instances[row];
            if (!lods.TryGetValue(instance.AssetId, out var tiers) || tiers.Count == 0) continue;
            var relative = resolveFocus?.Invoke(state, instance.Anchor) ?? localFocus?.Invoke(instance.Anchor) ?? new Vector3((float)((double)focus.X - instance.Anchor.X),
                (float)((double)focus.Y - instance.Anchor.Y), (float)((double)focus.Z - instance.Anchor.Z));
            float d2 = HorizontalDistanceSquared(instance.LocalTransform.M41, instance.LocalTransform.M43, relative);
            if (d2 > drawRadius * drawRadius) continue;
            var selection = SelectLods(d2, tiers.Count, lodDistances, crossfadeWidth);
            Submit(selection.Near, selection.Far >= 0 ? ComposeLodDissolve(selection.Transition, dissolveFloor).Near : dissolveFloor, 0);
            if (selection.Far >= 0) Submit(selection.Far, ComposeLodDissolve(selection.Transition, dissolveFloor).Far, 1);
            count++;
            void Submit(int tier, float dissolve, float complement)
            {
                var parts = tiers[tier].Parts;
                for (int part = 0; part < parts.Count; part++)
                    emit(state, instance, new RigidInstanceDraw(parts[part], instance.LocalTransform)
                    { Tint = Color.White, Material = Material.None, Dissolve = dissolve, DissolveComplement = complement, CastsShadows = castsShadows });
            }
        }
        return count;
    }

    // Every boundary uses the released two-tier interval and coverage kernel. More tiers add boundaries,
    // never a second fade interpretation. Empty thresholds keep the released LodDistance=0 behaviour.
    static float HorizontalDistanceSquared(float x, float z, Vector3 focus)
    {
        float dx = x - focus.X, dz = z - focus.Z;
        return dx * dx + dz * dz;
    }

    static (int Near, int Far, float Transition) SelectLods(float distanceSquared, int count, IReadOnlyList<float> distances, float width)
    {
        if (distances.Count == 0) return (0, -1, 0);
        if (distances.Count != count - 1) throw new ArgumentException("Every declared native tier requires its threshold.", nameof(distances));
        for (int tier = 0; tier < distances.Count; tier++)
        {
            float threshold = distances[tier];
            var boundary = SelectLodBoundary(distanceSquared, threshold, width);
            if (boundary.Far >= 0) return (tier, tier + 1, boundary.Transition);
            if (boundary.Near == 0) return (tier, -1, 0);
        }
        return (count - 1, -1, 0);
    }

    static (int Near, int Far, float Transition) SelectLodBoundary(float distanceSquared, float threshold, float width)
    {
        float half = MathF.Max(0, width) * .5f;
        float inner = MathF.Max(0, threshold - half), outer = threshold + half;
        if (outer > inner)
        {
            float distance = MathF.Sqrt(distanceSquared);
            if (distance < inner) return (0, -1, 0);
            if (distance < outer) return (0, 1, Math.Clamp((distance - inner) / (outer - inner), 0, 1));
            return (1, -1, 0);
        }
        return distanceSquared > threshold * threshold ? (1, -1, 0) : (0, -1, 0);
    }
}
