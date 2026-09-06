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

    const ulong JitterXSalt = 0x60B7A3D53B14C2A1UL;
    const ulong JitterZSalt = 0x91E10DA5C79E7B1DUL;
    const ulong DensitySalt = 0xD18A40B4F35062C7UL;
    const ulong ModelSalt = 0xA4FBCB9528163E09UL;
    const ulong ScaleSalt = 0x9C3C8D7E5F2A61B4UL;
    const ulong YawSalt = 0x3D17F0A58C6B42E9UL;
    const ulong RankSalt = 0xE2A86149B7D30C5FUL;

    /// <summary>Generates instances whose final jittered XZ positions fall in the half-open area.</summary>
    public static IReadOnlyList<GroundCoverInstance> Generate(
        RectArea area,
        GroundCoverSettings settings,
        Func<float, float, GroundCoverSample> surface)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(surface);
        Validate(area, settings, out float totalWeight);
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

        var result = new List<GroundCoverInstance>();
        for (long gzValue = gzLo; gzValue <= gzHi; gzValue++)
        {
            int gz = (int)gzValue;
            for (long gxValue = gxLo; gxValue <= gxHi; gxValue++)
            {
                int gx = (int)gxValue;
                float x = (gx + Hash01(gx, gz, settings.Seed, JitterXSalt) - 0.5f) * spacing;
                float z = (gz + Hash01(gx, gz, settings.Seed, JitterZSalt) - 0.5f) * spacing;
                if (x < area.MinX || x >= area.MaxX || z < area.MinZ || z >= area.MaxZ) continue;

                GroundCoverSample sample = surface(x, z);
                Vector3 normal = ValidateSample(sample);
                if (sample.Density == 0f || Hash01(gx, gz, settings.Seed, DensitySalt) >= sample.Density) continue;

                GroundCoverModel model = PickModel(settings.Models, totalWeight,
                    Hash01(gx, gz, settings.Seed, ModelSalt));
                float scale = settings.ScaleMin +
                    Hash01(gx, gz, settings.Seed, ScaleSalt) * (settings.ScaleMax - settings.ScaleMin);
                float yaw = Hash01(gx, gz, settings.Seed, YawSalt) * MathF.Tau;
                Vector3 position = new Vector3(x, sample.Height, z) + normal * settings.RootOffset;
                Matrix4x4 transform = SurfaceTransform(position, normal, scale, yaw);
                result.Add(new GroundCoverInstance(model.Id, position, transform,
                    Hash01(gx, gz, settings.Seed, RankSalt)));
            }
        }
        return result;
    }

    static void Validate(RectArea area, GroundCoverSettings settings, out float totalWeight)
    {
        if (!Finite(area.MinX) || !Finite(area.MinZ) || !Finite(area.MaxX) || !Finite(area.MaxZ) ||
            area.MaxX < area.MinX || area.MaxZ < area.MinZ)
            throw new ArgumentException("Ground cover area must be finite and ordered.", nameof(area));
        if (!Finite(settings.Spacing) || settings.Spacing <= 0f)
            throw new ArgumentException("GroundCoverSettings.Spacing must be finite and positive.", nameof(settings));
        if (!Finite(settings.ScaleMin) || !Finite(settings.ScaleMax) || settings.ScaleMin <= 0f ||
            settings.ScaleMax < settings.ScaleMin)
            throw new ArgumentException("Ground cover scales must be finite, positive and ordered.", nameof(settings));
        if (!Finite(settings.RootOffset))
            throw new ArgumentException("GroundCoverSettings.RootOffset must be finite.", nameof(settings));
        if (settings.Models is not { Length: > 0 })
            throw new ArgumentException("Ground cover requires at least one model.", nameof(settings));

        totalWeight = 0f;
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
    }

    static Vector3 ValidateSample(GroundCoverSample sample)
    {
        if (!Finite(sample.Height) || !Finite(sample.Density) || sample.Density < 0f || sample.Density > 1f ||
            !Finite(sample.Normal.X) || !Finite(sample.Normal.Y) || !Finite(sample.Normal.Z) ||
            sample.Normal.Y <= 0f || sample.Normal.LengthSquared() < 1e-12f)
            throw new ArgumentException("Ground cover surface samples require finite height, upward normal and density from 0 through 1.", nameof(sample));
        return Vector3.Normalize(sample.Normal);
    }

    static Matrix4x4 SurfaceTransform(Vector3 position, Vector3 normal, float scale, float yaw)
    {
        Vector3 reference = MathF.Abs(normal.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 right = Vector3.Normalize(Vector3.Cross(reference, normal));
        Vector3 forward = Vector3.Normalize(Vector3.Cross(normal, right));
        var alignment = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            normal.X, normal.Y, normal.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yaw) * alignment *
               Matrix4x4.CreateTranslation(position);
    }

    static GroundCoverModel PickModel(GroundCoverModel[] models, float totalWeight, float pick)
    {
        float target = pick * totalWeight;
        float running = 0f;
        for (int i = 0; i < models.Length; i++)
        {
            running += models[i].Weight;
            if (target < running) return models[i];
        }
        return models[^1];
    }

    static float Hash01(int x, int z, int seed, ulong salt)
    {
        unchecked
        {
            ulong key = ((ulong)(uint)x << 32) | (uint)z;
            ulong value = key ^ ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL) ^ salt;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (value >> 40) * (1f / 16_777_216f);
        }
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
