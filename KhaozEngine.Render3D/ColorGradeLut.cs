using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>An immutable 32 by 32 by 32 lattice of normalized display RGB values.</summary>
public sealed class ColorGradeLut
{
    public const int Size = 32;
    public const int SampleCount = Size * Size * Size;

    readonly Vector3[]? _samples;

    /// <summary>The exact identity mapping, which skips the GPU lookup.</summary>
    public static ColorGradeLut Identity { get; } = new();

    /// <summary>Whether every sample is the normalized RGB grid coordinate it occupies.</summary>
    public bool IsIdentity => _samples is null;

    ColorGradeLut() { }

    /// <param name="samples">RGB triples in [0, 1]. Red changes fastest, then green, then blue.</param>
    public ColorGradeLut(ReadOnlySpan<Vector3> samples)
    {
        if (samples.Length != SampleCount)
            throw new ArgumentException($"A colour cube requires {SampleCount} samples.", nameof(samples));

        bool identity = true;
        for (int i = 0; i < samples.Length; ++i)
        {
            Vector3 value = samples[i];
            if (!(value.X >= 0f && value.X <= 1f && value.Y >= 0f && value.Y <= 1f && value.Z >= 0f && value.Z <= 1f))
                throw new ArgumentOutOfRangeException(nameof(samples), i, "The display RGB sample at this index is outside [0, 1].");
            identity &= value == new Vector3((i % Size) / 31f, (i / Size % Size) / 31f, (i / (Size * Size)) / 31f);
        }

        if (!identity) _samples = samples.ToArray();
    }

    internal ReadOnlySpan<Vector3> Samples => _samples;
}
