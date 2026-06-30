using System;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// One already-decoded window/taskbar icon image: tightly-packed 8-bit RGBA (row-major, top-left origin,
    /// <c>Pixels.Length == Width * Height * 4</c>), the shape GLFW's <c>SetWindowIcon</c> wants. Decode-free on
    /// purpose so KhaozEngine.Windowing carries no image-decode dependency: the caller decodes a PNG (e.g. via
    /// <c>KhaozEngine.Render2D.ImageRgba</c> in the Game layer) and hands the pixels down. Supply several sizes
    /// (16/32/48...) and GLFW picks the closest for the current DPI; a single image is fine, GLFW will scale it.
    /// See <see cref="AppWindow.SetIcon"/> for the platform behaviour (Windows/Linux apply it, macOS is a no-op).
    /// </summary>
    public readonly struct WindowIcon
    {
        /// <summary>Tightly-packed RGBA8 pixels, row-major, top-left origin (length = Width*Height*4).</summary>
        public byte[] Pixels { get; }
        /// <summary>Icon width in pixels.</summary>
        public int Width { get; }
        /// <summary>Icon height in pixels.</summary>
        public int Height { get; }

        public WindowIcon(byte[] pixels, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "width/height must be positive.");
            int expected = width * height * 4;
            if (pixels.Length != expected)
                throw new ArgumentException($"pixels length {pixels.Length} != width*height*4 ({expected}).", nameof(pixels));
            Pixels = pixels; Width = width; Height = height;
        }
    }
}
