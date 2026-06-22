using System;
using System.IO;
using System.Reflection;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// The engine's bundled default font face (Roboto Regular, Apache-2.0), embedded in this assembly so a game
    /// never has to ship one or hard-code a system-font path (the macOS-only
    /// <c>/System/Library/Fonts/...</c> path that throws on Windows/Linux). Read the bytes once and bake a
    /// <see cref="SpriteFont"/> via any <c>LoadFont(byte[], ...)</c> / <c>LoadDefaultFont(...)</c> overload, or
    /// register them under a key with <see cref="FontManager"/>.
    /// </summary>
    public static class DefaultFont
    {
        // Deterministic logical name set on the &lt;EmbeddedResource&gt; in the csproj, so it does not depend on the
        // folder/file name MSBuild would otherwise derive.
        const string ResourceName = "KhaozEngine.Render2D.DefaultFont.ttf";
        static byte[]? _bytes;

        /// <summary>Raw TTF bytes of the embedded default face. Read from the assembly manifest on first use, then cached.</summary>
        public static byte[] Bytes => _bytes ??= ReadEmbedded();

        static byte[] ReadEmbedded()
        {
            Assembly asm = typeof(DefaultFont).Assembly;
            using Stream? s = asm.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded default font '{ResourceName}' was not found in assembly '{asm.GetName().Name}'.");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
