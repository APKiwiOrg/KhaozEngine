using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Per-stage toggles + parameters for the pixel post chain. Every stage is independent.</summary>
    public sealed class PixelPostProcessSettings
    {
        /// <summary>Low-res render target width. The scene renders here, then point-upscales to the window.</summary>
        public int LowResWidth = 320;
        /// <summary>Low-res render target height.</summary>
        public int LowResHeight = 180;

        /// <summary>Snap each pixel to the nearest color in <see cref="ActivePalette"/>.</summary>
        public bool Quantize = true;
        /// <summary>4x4 Bayer ordered dither applied with quantization.</summary>
        public bool Dither = true;
        /// <summary>Depth/normal discontinuity edge outline.</summary>
        public bool Outline = true;

        /// <summary>0 = smooth diffuse; N&gt;0 = cel shading with N light bands.</summary>
        public int CelBands = 4;

        public Palette ActivePalette = Palettes.Ember8;
        public Vector4 OutlineColor = new(0.05f, 0.03f, 0.06f, 1f);
        // Depth here is the non-linear clip depth (compressed near the far plane), so the threshold only
        // needs to catch genuine occlusion jumps between separate surfaces; the silhouette rim comes from
        // the normal edge (surface normal flips against the cleared background).
        public float OutlineDepthThreshold = 0.2f;
        public float OutlineNormalThreshold = 0.4f;

        /// <summary>Direction the sunlight travels (will be normalized).</summary>
        public Vector3 LightDirection = new(-0.5f, -1f, -0.35f);
        public Vector4 LightColor = new(1f, 0.96f, 0.9f, 1f);
        public Vector4 AmbientColor = new(0.22f, 0.24f, 0.30f, 1f);
    }
}
