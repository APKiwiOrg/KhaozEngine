using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>A few built-in retro palettes. Consumers can also pass their own <see cref="Palette"/>.</summary>
    public static class Palettes
    {
        static Vector4 H(uint v) => Palette.Hex(v);

        /// <summary>PICO-8 16-color.</summary>
        public static readonly Palette Pico8 = new("Pico8", new[]
        {
            H(0x000000), H(0x1D2B53), H(0x7E2553), H(0x008751), H(0xAB5236), H(0x5F574F), H(0xC2C3C7), H(0xFFF1E8),
            H(0xFF004D), H(0xFFA300), H(0xFFEC27), H(0x00E436), H(0x29ADFF), H(0x83769C), H(0xFF77A8), H(0xFFCCAA),
        });

        /// <summary>Game Boy 4-shade green.</summary>
        public static readonly Palette GameBoy = new("GameBoy", new[]
        {
            H(0x0F380F), H(0x306230), H(0x8BAC0F), H(0x9BBC0F),
        });

        /// <summary>Warm 8-color ramp.</summary>
        public static readonly Palette Ember8 = new("Ember8", new[]
        {
            H(0x1A1014), H(0x3A1F24), H(0x6E2B2B), H(0xA84A2B), H(0xD97A36), H(0xF2B05E), H(0xF7D9A0), H(0xFFF3D6),
        });

        public static readonly Palette[] All = { Pico8, GameBoy, Ember8 };
    }
}
