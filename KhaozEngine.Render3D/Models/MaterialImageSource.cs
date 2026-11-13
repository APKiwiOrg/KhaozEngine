using System;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D
{
    /// <summary>One distinct material image as the glTF loader found it: its identity, its dimensions, a private copy
    /// of its encoded file bytes, and its decoded RGBA8 pixels while something still needs them.
    /// <para>
    /// THE PIXELS ARE TRANSIENT. They are decoded when the image is first loaded, which is also what proves the image
    /// decodes (an undecodable image never becomes a source, so the map reads as absent). They are held from then
    /// until a scene has uploaded them, then held only weakly, so a resolver that keeps its parts for the life of a
    /// world keeps the compressed bytes per image and never the RGBA8. Any later reader (another scene, a re-upload
    /// after every user unloaded, a caller reading <see cref="DecodedImage.Rgba"/>) decodes again from the encoded
    /// bytes it kept.
    /// </para>
    /// <para>Thread-safe: the loader may run on worker threads while a scene uploads on the render thread.</para>
    /// </summary>
    internal sealed class MaterialImageSource
    {
        readonly object _gate = new();
        readonly byte[] _encoded;
        byte[]? _pinned;
        WeakReference<byte[]>? _released;
        int _decodes;

        MaterialImageSource(string identity, byte[] encoded, ImageRgba decoded)
        {
            Identity = identity;
            _encoded = encoded;
            Width = decoded.Width;
            Height = decoded.Height;
            _pinned = decoded.Pixels;
            _decodes = 1;
        }

        internal string Identity { get; }
        internal int Width { get; }
        internal int Height { get; }

        /// <summary>Decode <paramref name="encoded"/> into a new source, or null when it does not decode (a corrupt
        /// or unsupported image is an absent map, never a throw). The encoded bytes are copied, so the source never
        /// pins the buffer they were sliced from, such as a whole GLB binary chunk.</summary>
        internal static MaterialImageSource? TryCreate(string identity, ReadOnlySpan<byte> encoded)
        {
            ImageRgba decoded;
            try
            {
                decoded = ImageRgba.Decode(encoded);
            }
            catch
            {
                return null;
            }
            return new MaterialImageSource(identity, encoded.ToArray(), decoded);
        }

        /// <summary>The decoded pixels: the held array, a released array something else still holds, or a fresh
        /// decode of the kept encoded bytes.</summary>
        internal byte[] Pixels()
        {
            lock (_gate)
            {
                if (_pinned is { } pinned) return pinned;
                if (_released is not null && _released.TryGetTarget(out byte[]? alive)) return alive;
                byte[] decoded = ImageRgba.Decode(_encoded).Pixels;
                _decodes++;
                _released = new WeakReference<byte[]>(decoded);
                return decoded;
            }
        }

        /// <summary>A scene uploaded these pixels. Stop holding them, so only a reader that still needs them keeps
        /// them alive.</summary>
        internal void ReleaseAfterUpload()
        {
            lock (_gate)
            {
                if (_pinned is null) return;
                _released = new WeakReference<byte[]>(_pinned);
                _pinned = null;
            }
        }

        /// <summary>Whether this source still holds its decoded pixels strongly, which it does from decode until the
        /// first upload.</summary>
        internal bool PixelsPinnedForTests
        {
            get { lock (_gate) return _pinned is not null; }
        }

        /// <summary>How many times this source has decoded its image, the load's own decode included.</summary>
        internal int DecodeCountForTests
        {
            get { lock (_gate) return _decodes; }
        }
    }
}
