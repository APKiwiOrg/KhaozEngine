using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Terrain;

/// <summary>A sampled ground-cover surface point. Density is normalized to the range 0 through 1.</summary>
public readonly record struct GroundCoverSample(float Height, Vector3 Normal, float Density);

/// <summary>One weighted model in a ground-cover distribution.</summary>
public readonly record struct GroundCoverModel(string Id, float Weight);

/// <summary>Deterministic ground-cover placement settings.</summary>
public sealed class GroundCoverSettings
{
    /// <summary>Content seed mixed into every candidate channel.</summary>
    public int Seed { get; set; }
    /// <summary>World-space distance between candidate cells.</summary>
    public float Spacing { get; set; } = 1f;
    /// <summary>Smallest uniform instance scale.</summary>
    public float ScaleMin { get; set; } = 1f;
    /// <summary>Largest uniform instance scale.</summary>
    public float ScaleMax { get; set; } = 1f;
    /// <summary>Distance along the sampled normal from the surface to the model root.</summary>
    public float RootOffset { get; set; }
    /// <summary>Weighted model ids. At least one positive finite weight is required.</summary>
    public GroundCoverModel[] Models { get; set; } = Array.Empty<GroundCoverModel>();
}

/// <summary>One generated ground-cover instance with its surface-aligned transform and stable thinning rank.</summary>
public readonly record struct GroundCoverInstance(
    string ModelId,
    Vector3 Position,
    Matrix4x4 Transform,
    float ThinningRank);

/// <summary>Deterministic bounded ground-cover generation over any sampled height surface.</summary>
public static class GroundCoverDistribution
{
    /// <summary>Maximum candidate cells accepted by one call.</summary>
    public const int MaxCandidateCount = 4_000_000;

    /// <summary>Generates instances whose final jittered XZ positions fall in the half-open area.</summary>
    public static IReadOnlyList<GroundCoverInstance> Generate(
        RectArea area,
        GroundCoverSettings settings,
        Func<float, float, GroundCoverSample> surface)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(surface);
        return Run(area, settings,
            candidate => new GroundCoverSurfaceInput(surface(candidate.WorldX, candidate.WorldZ)), null);
    }

    /// <summary>Generates exactly what <see cref="Generate"/> would over a candidate-aware surface, reporting every
    /// scanned candidate's decision to the optional observer in scan order (Z then X). The surface is called once
    /// per candidate inside the half-open area, in the same order and count as <see cref="Generate"/>. A null
    /// sample is an outside-domain rejection. A surface that cannot answer throws to the caller, never reporting a
    /// substitute sample. Observed rows are provisional until the caller's enclosing result is complete.</summary>
    public static IReadOnlyList<GroundCoverInstance> GenerateTraced(
        RectArea area,
        GroundCoverSettings settings,
        Func<GroundCoverCandidate, GroundCoverSurfaceInput> surface,
        Action<GroundCoverDecision>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(surface);
        return Run(area, settings, surface, observe);
    }

    /// <summary>Validates and copies settings, including model order, into an immutable single-address evaluator.</summary>
    public static GroundCoverCandidateEvaluator Prepare(GroundCoverSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GroundCoverCandidateEvaluator(settings, ValidateSettings(settings));
    }

    static IReadOnlyList<GroundCoverInstance> Run(
        RectArea area,
        GroundCoverSettings settings,
        Func<GroundCoverCandidate, GroundCoverSurfaceInput> surface,
        Action<GroundCoverDecision>? observe)
    {
        ValidateArea(area);
        float totalWeight = ValidateSettings(settings);
        if (area.MinX == area.MaxX || area.MinZ == area.MaxZ) return Array.Empty<GroundCoverInstance>();

        float spacing = settings.Spacing;
        int gxLo = CheckedFloor(area.MinX / spacing - 0.5f);
        int gxHi = CheckedCeiling(area.MaxX / spacing + 0.5f);
        int gzLo = CheckedFloor(area.MinZ / spacing - 0.5f);
        int gzHi = CheckedCeiling(area.MaxZ / spacing + 0.5f);
        long candidateWidth = (long)gxHi - gxLo + 1;
        long candidateHeight = (long)gzHi - gzLo + 1;
        if (candidateWidth > MaxCandidateCount || candidateHeight > MaxCandidateCount ||
            candidateWidth > MaxCandidateCount / candidateHeight)
            throw new ArgumentException($"Ground cover query exceeds the {MaxCandidateCount} candidate limit.", nameof(area));

        var evaluator = new GroundCoverCandidateEvaluator(settings, totalWeight);
        var result = new List<GroundCoverInstance>();
        for (long gzValue = gzLo; gzValue <= gzHi; gzValue++)
        {
            int gz = (int)gzValue;
            for (long gxValue = gxLo; gxValue <= gxHi; gxValue++)
            {
                GroundCoverCandidate candidate = evaluator.Candidate((int)gxValue, gz);
                float x = candidate.WorldX;
                float z = candidate.WorldZ;
                if (x < area.MinX || x >= area.MaxX || z < area.MinZ || z >= area.MaxZ)
                {
                    observe?.Invoke(evaluator.OutsideArea(candidate));
                    continue;
                }

                GroundCoverDecision decision = evaluator.Decide(candidate, surface(candidate));
                observe?.Invoke(decision);
                if (decision.Instance is { } instance) result.Add(instance);
            }
        }
        return result;
    }

    static void ValidateArea(RectArea area)
    {
        if (!Finite(area.MinX) || !Finite(area.MinZ) || !Finite(area.MaxX) || !Finite(area.MaxZ) ||
            area.MaxX < area.MinX || area.MaxZ < area.MinZ)
            throw new ArgumentException("Ground cover area must be finite and ordered.", nameof(area));
    }

    static float ValidateSettings(GroundCoverSettings settings)
    {
        if (!Finite(settings.Spacing) || settings.Spacing <= 0f)
            throw new ArgumentException("GroundCoverSettings.Spacing must be finite and positive.", nameof(settings));
        if (!Finite(settings.ScaleMin) || !Finite(settings.ScaleMax) || settings.ScaleMin <= 0f ||
            settings.ScaleMax < settings.ScaleMin)
            throw new ArgumentException("Ground cover scales must be finite, positive and ordered.", nameof(settings));
        if (!Finite(settings.RootOffset))
            throw new ArgumentException("GroundCoverSettings.RootOffset must be finite.", nameof(settings));
        if (settings.Models is not { Length: > 0 })
            throw new ArgumentException("Ground cover requires at least one model.", nameof(settings));

        float totalWeight = 0f;
        for (int i = 0; i < settings.Models.Length; i++)
        {
            GroundCoverModel model = settings.Models[i];
            if (string.IsNullOrWhiteSpace(model.Id))
                throw new ArgumentException("Ground cover model ids cannot be blank.", nameof(settings));
            if (!Finite(model.Weight) || model.Weight <= 0f)
                throw new ArgumentException($"Ground cover model '{model.Id}' must have a finite positive weight.", nameof(settings));
            totalWeight += model.Weight;
        }
        if (!Finite(totalWeight))
            throw new ArgumentException("Ground cover model weights exceed the supported total.", nameof(settings));
        return totalWeight;
    }

    static int CheckedFloor(float value)
    {
        double floored = Math.Floor(value);
        if (floored < int.MinValue || floored > int.MaxValue)
            throw new ArgumentException("Ground cover area exceeds supported coordinates.");
        return (int)floored;
    }

    static int CheckedCeiling(float value)
    {
        double ceiling = Math.Ceiling(value);
        if (ceiling < int.MinValue || ceiling > int.MaxValue)
            throw new ArgumentException("Ground cover area exceeds supported coordinates.");
        return (int)ceiling;
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
