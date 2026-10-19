using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Terrain;

/// <summary>Evaluates single ground-cover lattice addresses with settings copied and validated once by
/// <see cref="GroundCoverDistribution.Prepare"/>. It applies no area clipping, so a caller can own a lattice
/// address wherever its jittered point lands. <see cref="GroundCoverDistribution.Generate"/> and
/// <see cref="GroundCoverDistribution.GenerateTraced"/> share this arithmetic kernel.</summary>
public sealed class GroundCoverCandidateEvaluator
{
    const ulong JitterXSalt = 0x60B7A3D53B14C2A1UL;
    const ulong JitterZSalt = 0x91E10DA5C79E7B1DUL;
    const ulong DensitySalt = 0xD18A40B4F35062C7UL;
    const ulong ModelSalt = 0xA4FBCB9528163E09UL;
    const ulong ScaleSalt = 0x9C3C8D7E5F2A61B4UL;
    const ulong YawSalt = 0x3D17F0A58C6B42E9UL;
    const ulong RankSalt = 0xE2A86149B7D30C5FUL;

    readonly GroundCoverModel[] _models;
    readonly float _totalWeight;

    internal GroundCoverCandidateEvaluator(GroundCoverSettings settings, float totalWeight)
    {
        Seed = settings.Seed;
        Spacing = settings.Spacing;
        ScaleMin = settings.ScaleMin;
        ScaleMax = settings.ScaleMax;
        RootOffset = settings.RootOffset;
        _models = (GroundCoverModel[])settings.Models.Clone();
        Models = Array.AsReadOnly(_models);
        _totalWeight = totalWeight;
    }

    /// <summary>Content seed mixed into every channel.</summary>
    public int Seed { get; }
    /// <summary>World-space distance between lattice addresses.</summary>
    public float Spacing { get; }
    /// <summary>Smallest uniform instance scale.</summary>
    public float ScaleMin { get; }
    /// <summary>Largest uniform instance scale.</summary>
    public float ScaleMax { get; }
    /// <summary>Distance along the sampled normal from the surface to the model root.</summary>
    public float RootOffset { get; }
    /// <summary>Weighted models in their pick order.</summary>
    public IReadOnlyList<GroundCoverModel> Models { get; }

    /// <summary>The candidate at one lattice address with all seven channels.</summary>
    public GroundCoverCandidate Candidate(int x, int z)
    {
        float jitterX = Hash01(x, z, Seed, JitterXSalt);
        float jitterZ = Hash01(x, z, Seed, JitterZSalt);
        return new GroundCoverCandidate(x, z,
            (x + jitterX - 0.5f) * Spacing,
            (z + jitterZ - 0.5f) * Spacing,
            jitterX, jitterZ,
            Hash01(x, z, Seed, DensitySalt),
            Hash01(x, z, Seed, ModelSalt),
            Hash01(x, z, Seed, ScaleSalt),
            Hash01(x, z, Seed, YawSalt),
            Hash01(x, z, Seed, RankSalt));
    }

    /// <summary>Decides one lattice address over the caller's surface, calling it exactly once. A surface that
    /// cannot answer throws to the caller rather than reporting a sample.</summary>
    public GroundCoverDecision Evaluate(int x, int z, Func<GroundCoverCandidate, GroundCoverSurfaceInput> surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        GroundCoverCandidate candidate = Candidate(x, z);
        return Decide(candidate, surface(candidate));
    }

    internal GroundCoverDecision OutsideArea(in GroundCoverCandidate candidate) => new(candidate,
        GroundCoverDecisionKind.OutsideArea, null, null, Scale(candidate), Yaw(candidate));

    internal GroundCoverDecision Decide(in GroundCoverCandidate candidate, GroundCoverSurfaceInput input)
    {
        float scale = Scale(candidate);
        float yaw = Yaw(candidate);
        if (input.Sample is not { } sample)
            return new(candidate, GroundCoverDecisionKind.OutsideSurface, null, null, scale, yaw);

        Vector3 normal = ValidateSample(sample);
        if (sample.Density == 0f)
            return new(candidate, GroundCoverDecisionKind.ZeroDensity, sample, null, scale, yaw);
        if (candidate.DensityPick >= sample.Density)
            return new(candidate, GroundCoverDecisionKind.DensityRejected, sample, null, scale, yaw);

        GroundCoverModel model = PickModel(candidate.ModelPick);
        Vector3 position = new Vector3(candidate.WorldX, sample.Height, candidate.WorldZ) + normal * RootOffset;
        Matrix4x4 transform = SurfaceTransform(position, normal, scale, yaw);
        var instance = new GroundCoverInstance(model.Id, position, transform, candidate.Rank);
        return new(candidate, GroundCoverDecisionKind.Accepted, sample, instance, scale, yaw);
    }

    float Scale(in GroundCoverCandidate candidate) => ScaleMin + candidate.ScalePick * (ScaleMax - ScaleMin);

    static float Yaw(in GroundCoverCandidate candidate) => candidate.YawPick * MathF.Tau;

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
        Vector3 forward = Vector3.Normalize(Vector3.Cross(right, normal));
        var alignment = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            normal.X, normal.Y, normal.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yaw) * alignment *
               Matrix4x4.CreateTranslation(position);
    }

    GroundCoverModel PickModel(float pick)
    {
        float target = pick * _totalWeight;
        float running = 0f;
        for (int i = 0; i < _models.Length; i++)
        {
            running += _models[i].Weight;
            if (target < running) return _models[i];
        }
        return _models[^1];
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

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
