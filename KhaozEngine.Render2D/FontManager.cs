using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Registers fonts by key and resolves them to raw TTF bytes - the font analogue of
    /// <c>AudioSystem.RegisterSfx</c>/<c>IsSfxLoaded</c>. The engine's bundled face is pre-registered under the
    /// reserved <see cref="DefaultKey"/>; a game overrides it (or adds its own faces) by registering a key, either
    /// from a content directory (<c>{ContentDirectory}/{key}.ttf</c> or <c>.otf</c>; key == path under the dir
    /// without extension, matching the SFX probe convention) or from raw bytes it loaded itself.
    /// <para>
    /// Resolution is GPU-free, so the manager is headless-testable. Build a <see cref="SpriteFont"/> from a
    /// resolved key with a surface's <c>LoadFont(byte[], ...)</c> overload, or the turn-key
    /// <c>LoadFont(FontManager, key, ...)</c> sugar on <see cref="Render2DSurface"/>/<see cref="Render2DContext"/>.
    /// </para>
    /// </summary>
    public sealed class FontManager
    {
        /// <summary>Reserved key for the engine's bundled default face (pre-registered to <see cref="DefaultFont.Bytes"/>).</summary>
        public const string DefaultKey = "default";

        // Probed in order under ContentDirectory; first match wins (mirrors AudioSystem's SFX extension probe).
        static readonly string[] FontExtensions = { ".ttf", ".otf" };

        readonly Dictionary<string, byte[]> _fonts = new(StringComparer.Ordinal);

        /// <summary>Directory probed by <see cref="RegisterFont(string)"/>. Defaults to <c>{BaseDirectory}/assets/fonts</c>.</summary>
        public string ContentDirectory { get; }

        /// <param name="contentDirectory">
        /// Root probed by <see cref="RegisterFont(string)"/>. Defaults to <c>{AppContext.BaseDirectory}/assets/fonts</c>.
        /// </param>
        public FontManager(string? contentDirectory = null)
        {
            ContentDirectory = contentDirectory ?? Path.Combine(AppContext.BaseDirectory, "assets", "fonts");
            _fonts[DefaultKey] = DefaultFont.Bytes;
        }

        /// <summary>True if a font (engine default or game-registered) is registered under <paramref name="key"/>.</summary>
        public bool IsFontRegistered(string key) => _fonts.ContainsKey(key);

        /// <summary>
        /// Register raw TTF bytes under <paramref name="key"/>, replacing any existing registration - so a game can
        /// override a reserved key (e.g. <see cref="DefaultKey"/>) with its own face, or register a font it loaded itself.
        /// </summary>
        public void RegisterFont(string key, byte[] ttf)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            ArgumentNullException.ThrowIfNull(ttf);
            _fonts[key] = ttf;
        }

        /// <summary>
        /// Register a font found under <see cref="ContentDirectory"/> by probing <c>{key}.ttf</c> then <c>{key}.otf</c>
        /// (key == path under the dir without extension, e.g. <c>"ui/title"</c>). Throws if no file is found.
        /// </summary>
        public void RegisterFont(string key)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            foreach (string ext in FontExtensions)
            {
                string path = Path.Combine(ContentDirectory, key + ext);
                if (!File.Exists(path)) continue;
                _fonts[key] = File.ReadAllBytes(path);
                return;
            }
            throw new FileNotFoundException(
                $"FontManager: no TTF/OTF file for key '{key}' under '{ContentDirectory}'.");
        }

        /// <summary>Resolve <paramref name="key"/> to its raw TTF bytes. Throws if the key is not registered.</summary>
        public byte[] GetFontBytes(string key) =>
            _fonts.TryGetValue(key, out byte[]? ttf)
                ? ttf
                : throw new KeyNotFoundException($"FontManager: no font registered under key '{key}'.");

        /// <summary>Try-resolve <paramref name="key"/> to its raw TTF bytes.</summary>
        public bool TryGetFontBytes(string key, [NotNullWhen(true)] out byte[]? ttf) =>
            _fonts.TryGetValue(key, out ttf);
    }
}
