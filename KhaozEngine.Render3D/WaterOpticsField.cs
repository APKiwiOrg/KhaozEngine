using System;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// An immutable row-major lattice of signed bed depths relative to a water body's reference surface.
    /// Positive samples are water depth. Zero and negative samples are known bank or land.
    /// </summary>
    public sealed class WaterOpticsField
    {
        /// <summary>Largest supported sample count along either lattice axis.</summary>
        public const int MaxDimension = 1024;

        private readonly float[] _restDepths;

        /// <summary>Validates and copies the complete finite signed depth payload.</summary>
        public WaterOpticsField(int width, int height, ReadOnlySpan<float> restDepths)
        {
            if (width < 2 || width > MaxDimension)
                throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be between 2 and 1024.");
            if (height < 2 || height > MaxDimension)
                throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be between 2 and 1024.");

            int sampleCount = checked(width * height);
            if (restDepths.Length != sampleCount)
                throw new ArgumentException("The payload must contain exactly width times height samples.", nameof(restDepths));
            foreach (float depth in restDepths)
            {
                if (!float.IsFinite(depth))
                    throw new ArgumentException("Every rest depth must be finite.", nameof(restDepths));
            }

            Width = width;
            Height = height;
            SampleBytes = checked((long)sampleCount * sizeof(float));
            _restDepths = restDepths.ToArray();
        }

        /// <summary>Number of samples along local X.</summary>
        public int Width { get; }

        /// <summary>Number of sample rows along local Z.</summary>
        public int Height { get; }

        /// <summary>Owned signed samples indexed as <c>z * Width + x</c>.</summary>
        public ReadOnlySpan<float> RestDepths => _restDepths;

        /// <summary>Checked storage size of the float32 sample payload in bytes.</summary>
        public long SampleBytes { get; }

        /// <summary>
        /// Bilinearly samples finite normalized coordinates in [0, 1], preserving signed bank depths.
        /// The final row and column use the last two valid samples with weight one at the outer edge.
        /// </summary>
        public float SampleDepth(float u, float v)
        {
            if (!float.IsFinite(u) || u < 0f || u > 1f)
                throw new ArgumentOutOfRangeException(nameof(u), u, "The coordinate must be finite and between 0 and 1.");
            if (!float.IsFinite(v) || v < 0f || v > 1f)
                throw new ArgumentOutOfRangeException(nameof(v), v, "The coordinate must be finite and between 0 and 1.");

            float sx = u * (Width - 1);
            float sz = v * (Height - 1);
            int x0 = Math.Min((int)MathF.Floor(sx), Width - 2);
            int z0 = Math.Min((int)MathF.Floor(sz), Height - 2);
            float tx = sx - x0;
            float tz = sz - z0;
            int row0 = z0 * Width;
            int row1 = (z0 + 1) * Width;

            // Weighted sums avoid overflowing the difference between finite samples of opposite signs.
            float near = (1f - tx) * _restDepths[row0 + x0] + tx * _restDepths[row0 + x0 + 1];
            float far = (1f - tx) * _restDepths[row1 + x0] + tx * _restDepths[row1 + x0 + 1];
            return (1f - tz) * near + tz * far;
        }
    }
}
