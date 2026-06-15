using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Post-process toggles + parameters. Every stage is independent. Defaults target a smooth, stylized
    /// (non-retro) space look: high internal render resolution, anti-aliased downscale, dark background,
    /// smooth diffuse, no palette/dither/cel. Flip the toggles for the chunky retro/pixel look.
    /// </summary>
    public sealed class PixelPostProcessSettings
    {
        /// <summary>Internal render width. High = smooth; small + Pixelated = chunky retro pixels.</summary>
        public int RenderWidth = 1600;
        /// <summary>Internal render height.</summary>
        public int RenderHeight = 900;

        /// <summary>Point-sample the final upscale for crisp pixels (retro). False = smooth/AA downscale.</summary>
        public bool Pixelated = false;

        /// <summary>Snap each pixel to the nearest color in <see cref="ActivePalette"/> (retro).</summary>
        public bool Quantize = false;
        /// <summary>4x4 Bayer ordered dither applied with quantization (retro).</summary>
        public bool Dither = false;
        /// <summary>Depth/normal discontinuity edge outline (stylized).</summary>
        public bool Outline = true;
        /// <summary>Procedural starfield in the background (assumes a dark space background).</summary>
        public bool Starfield = true;

        /// <summary>0 = smooth diffuse; N&gt;0 = cel shading with N light bands (retro/toon).</summary>
        public int CelBands = 0;

        public Palette ActivePalette = Palettes.Ember8;
        public Vector4 OutlineColor = new(0.02f, 0.02f, 0.04f, 1f);
        // Clip depth is non-linear (compressed near the far plane); the threshold only needs to catch
        // genuine occlusion jumps. The silhouette rim comes from the normal edge (normal flips vs background).
        public float OutlineDepthThreshold = 0.2f;
        public float OutlineNormalThreshold = 0.45f;

        /// <summary>Scene background (cleared behind the model). Dark = space.</summary>
        public Vector4 BackgroundColor = new(0.02f, 0.03f, 0.06f, 1f);

        /// <summary>Direction the sunlight travels (will be normalized).</summary>
        public Vector3 LightDirection = new(-0.5f, -0.85f, -0.35f);
        public Vector4 LightColor = new(1f, 0.95f, 0.86f, 1f);
        public Vector4 AmbientColor = new(0.16f, 0.19f, 0.30f, 1f);
    }
}
