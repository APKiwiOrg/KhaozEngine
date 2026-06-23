using System;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Back-compat shim for the engine's dependency-free PNG encoder, which now lives in the BCL-only
    /// <c>KhaozEngine.Imaging</c> package as <see cref="KhaozEngine.Imaging.PngWriter"/>. Existing callers of
    /// <see cref="Encode"/>/<see cref="Write"/> keep working; new code should prefer <c>PngWriter</c> directly.
    /// </summary>
    public static class Png
    {
        /// <inheritdoc cref="KhaozEngine.Imaging.PngWriter.Encode"/>
        public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height) =>
            KhaozEngine.Imaging.PngWriter.Encode(rgba, width, height);

        /// <inheritdoc cref="KhaozEngine.Imaging.PngWriter.Save"/>
        public static void Write(string path, ReadOnlySpan<byte> rgba, int width, int height) =>
            KhaozEngine.Imaging.PngWriter.Save(path, rgba, width, height);
    }
}
