using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

/// <summary>How cover disappears before its distance cutoff.</summary>
public enum GroundCoverFadeMode
{
    /// <summary>Discard fragments using the shared world-space dissolve mask.</summary>
    Dissolve,
    /// <summary>Smoothly lower blades along their local up axis while keeping their roots fixed.</summary>
    HeightScale,
}

/// <summary>Live draw and thinning policy for ground cover.</summary>
public sealed class GroundCoverRenderOptions
{
    public float DrawRadius { get; set; } = 40f;
    /// <summary>End of density thinning in metres. Null follows DrawRadius. Beyond this radius only
    /// DistantDensity remains, so extending the horizon need not enlarge the dense area.</summary>
    public float? DensityRadius { get; set; }
    public float FadeBandWidth { get; set; } = 8f;
    public float QualityDensity { get; set; } = 1f;
    public float DistantDensity { get; set; } = 0.35f;
    public bool CastsShadows { get; set; }
    /// <summary>Width in metres of each thinning placement's fade. The layer fade band bounds it.</summary>
    public float InstanceFadeBandWidth { get; set; } = 1f;
    /// <summary>Defaults to the original fragment dissolve. Height scaling suits short rooted foliage.</summary>
    public GroundCoverFadeMode FadeMode { get; set; }
}

/// <summary>Queues precomputed ground-cover transforms through the rigid instancing path.</summary>
public static class GroundCoverRenderer
{
    /// <summary>Queues each surviving placement and all of its model parts. Returns placements drawn.</summary>
    public static int Queue(
        SceneInstances instances,
        IReadOnlyList<GroundCoverInstance> cover,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes,
        Vector3 focus,
        GroundCoverRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(instances);
        return Emit(cover, meshes, focus, options, instances, QueuePart);
    }

    /// <summary>Scene3D convenience over <see cref="Queue"/>.</summary>
    public static int DrawGroundCover(
        this Scene3D scene,
        IReadOnlyList<GroundCoverInstance> cover,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes,
        Vector3 focus,
        GroundCoverRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Emit(cover, meshes, focus, options, scene, DrawPart);
    }

    static readonly Action<SceneInstances, MeshHandle, Matrix4x4, float, bool> QueuePart =
        static (instances, mesh, transform, dissolve, castsShadows) =>
            instances.Add(mesh, transform, Color.White, Material.None, dissolve, 0f, default, castsShadows);

    static readonly Action<Scene3D, MeshHandle, Matrix4x4, float, bool> DrawPart =
        static (scene, mesh, transform, dissolve, castsShadows) =>
            scene.Draw(mesh, transform, Color.White, Material.None, dissolve, 0f, default, castsShadows);

    static int Emit<TState>(
        IReadOnlyList<GroundCoverInstance> cover,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes,
        Vector3 focus,
        GroundCoverRenderOptions options,
        TState state,
        Action<TState, MeshHandle, Matrix4x4, float, bool> emit)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        if (options.QualityDensity == 0f) return 0;

        float radius = options.DrawRadius;
        float radius2 = radius * radius;
        float densityRadius = MathF.Min(options.DensityRadius ?? radius, radius);
        float band = MathF.Min(options.FadeBandWidth, densityRadius);
        float inner = densityRadius - band;
        float distant = MathF.Min(options.DistantDensity, options.QualityDensity);
        GroundCoverBatch? batch = cover as GroundCoverBatch;
        int drawn = 0;
        for (int i = 0; i < cover.Count; i++)
        {
            if (batch is not null)
            {
                i = batch.SkipOutside(i, focus, radius2);
                if (i == cover.Count) break;
            }
            GroundCoverInstance instance = batch is null ? cover[i] : batch.Items[i];
            if (instance.ThinningRank < 0f || instance.ThinningRank >= options.QualityDensity) continue;
            float dx = instance.Position.X - focus.X;
            float dz = instance.Position.Z - focus.Z;
            float distance2 = dx * dx + dz * dz;
            if (distance2 > radius2) continue;

            float distance = MathF.Sqrt(distance2);
            float cutoff = radius;
            float dissolveStart = MathF.Max(0f, radius - options.FadeBandWidth);
            if (options.QualityDensity > distant && instance.ThinningRank >= distant)
            {
                float progress = (options.QualityDensity - instance.ThinningRank) /
                                 (options.QualityDensity - distant);
                cutoff = inner + progress * band;
                float personalBand = MathF.Min(options.InstanceFadeBandWidth, band);
                dissolveStart = MathF.Max(inner, cutoff - personalBand);
            }
            if (distance > cutoff) continue;
            if (!meshes.TryGetValue(instance.ModelId, out IReadOnlyList<MeshHandle>? parts) || parts is null) continue;
            float dissolve = cutoff > dissolveStart
                ? Math.Clamp((distance - dissolveStart) / (cutoff - dissolveStart), 0f, 1f)
                : 0f;
            Matrix4x4 transform = instance.Transform;
            if (options.FadeMode == GroundCoverFadeMode.HeightScale && dissolve > 0f)
            {
                // Only local up changes. Roots and footprint stay fixed, including on slopes.
                float height = 1f - dissolve * dissolve * (3f - 2f * dissolve);
                if (height <= 0.0001f) continue;
                transform.M21 *= height;
                transform.M22 *= height;
                transform.M23 *= height;
                dissolve = 0f;
            }
            for (int part = 0; part < parts.Count; part++)
                emit(state, parts[part], transform, dissolve, options.CastsShadows);
            drawn++;
        }
        return drawn;
    }

    static void Validate(GroundCoverRenderOptions options)
    {
        if (!Finite(options.DrawRadius) || options.DrawRadius < 0f)
            throw new ArgumentException("Ground cover draw radius must be finite and non-negative.", nameof(options));
        if (options.DensityRadius is float densityRadius && (!Finite(densityRadius) || densityRadius < 0f))
            throw new ArgumentException("Ground cover density radius must be finite and non-negative.", nameof(options));
        if (!Finite(options.FadeBandWidth) || options.FadeBandWidth < 0f)
            throw new ArgumentException("Ground cover fade band must be finite and non-negative.", nameof(options));
        if (!Finite(options.InstanceFadeBandWidth) || options.InstanceFadeBandWidth < 0f)
            throw new ArgumentException("Ground cover instance fade band must be finite and non-negative.", nameof(options));
        if (options.FadeMode is not GroundCoverFadeMode.Dissolve and not GroundCoverFadeMode.HeightScale)
            throw new ArgumentException("Unknown ground cover fade mode.", nameof(options));
        if (!Unit(options.QualityDensity) || !Unit(options.DistantDensity))
            throw new ArgumentException("Ground cover densities must be finite values from 0 through 1.", nameof(options));
    }

    static bool Unit(float value) => Finite(value) && value >= 0f && value <= 1f;
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
