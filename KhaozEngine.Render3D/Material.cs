using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Per-instance surface material for the model pass: self-illumination (<see cref="Emissive"/>) added after
    /// lighting, plus Blinn-Phong specular controlled by <see cref="Specular"/> strength and
    /// <see cref="Shininess"/> exponent. Default (<see cref="None"/>) is the current matte look (no glow, no
    /// shine). Specular colour comes from the key light; this struct only carries the per-instance amount/shape.
    /// </summary>
    public readonly struct Material
    {
        /// <summary>Self-illumination added after lighting (default zero = none).</summary>
        public Vector4 Emissive { get; }

        /// <summary>Blinn-Phong specular strength 0..1 (default 0 = matte).</summary>
        public float Specular { get; }

        /// <summary>Specular exponent (default 32).</summary>
        public float Shininess { get; }

        public Material(Vector4 emissive, float specular, float shininess)
        {
            Emissive = emissive;
            Specular = specular;
            Shininess = shininess;
        }

        /// <summary>Emissive 0, specular 0, shininess 32 — the current matte look.</summary>
        public static Material None => new(Vector4.Zero, 0f, 32f);

        /// <summary>Glow with the given emissive colour, no specular. (Named <c>Glowing</c> rather than
        /// <c>Emissive</c> because the <see cref="Emissive"/> property already occupies that name.)</summary>
        public static Material Glowing(Vector4 color) => new(color, 0f, 32f);

        /// <summary>Specular highlight (no glow) with the given strength and exponent.</summary>
        public static Material Shiny(float specular, float shininess = 48f) => new(Vector4.Zero, specular, shininess);
    }
}
