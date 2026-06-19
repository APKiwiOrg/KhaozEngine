using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>A swappable color palette for quantization. Colors are RGBA 0..1.</summary>
    public sealed class Palette
    {
        public string Name { get; }
        public Color[] Colors { get; }
        public Palette(string name, Color[] colors) { Name = name; Colors = colors; }

        /// <summary>Build an opaque color from a 0xRRGGBB hex literal.</summary>
        public static Color Hex(uint rgb) =>
            new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}
