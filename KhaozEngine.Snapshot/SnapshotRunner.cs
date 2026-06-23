using System;
using System.IO;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Snapshot
{
    /// <summary>
    /// Headless named-shot runner: capture -> PNG encode -> write <c>&lt;OutDir&gt;/&lt;name&gt;.png</c> -> log the
    /// path. A game's screenshot tool builds its own scenes inside the shot callbacks; this absorbs the
    /// capture/encode/write/log boilerplate. Deterministic and window-free (the underlying capture still needs a
    /// GPU device). The 3D <c>Shot3D</c> method lives in <c>KhaozEngine.Snapshot.Render3D</c> so a 2D-only game
    /// never drags in Render3D.
    /// </summary>
    public sealed class SnapshotRunner
    {
        readonly Action<string> _log;

        /// <summary>Directory every shot is written to (created by the constructor).</summary>
        public string OutDir { get; }

        /// <summary>Number of shots written so far.</summary>
        public int Count { get; private set; }

        /// <summary>Creates <paramref name="outDir"/> and routes each shot's path to <paramref name="log"/> (default <see cref="Console.WriteLine(string)"/>).</summary>
        public SnapshotRunner(string outDir, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(outDir)) throw new ArgumentException("outDir must be non-empty.", nameof(outDir));
            OutDir = outDir;
            _log = log ?? Console.WriteLine;
            Directory.CreateDirectory(outDir);
        }

        /// <summary>Capture a 2D scene and save it as <c>&lt;OutDir&gt;/&lt;name&gt;.png</c>; returns the written path.</summary>
        public string Shot2D(string name, int width, int height, Color clear, Action<Render2DContext> draw)
        {
            byte[] rgba = Render2DSnapshot.Capture(width, height, clear, draw);
            return Save(name, rgba, width, height);
        }

        /// <summary>
        /// Encode an already-captured RGBA8 buffer to <c>&lt;OutDir&gt;/&lt;name&gt;.png</c>, log the path, bump
        /// <see cref="Count"/>, and return the path. The shared sink used by <see cref="Shot2D"/> and the
        /// <c>Shot3D</c> extension (and usable directly for a buffer captured by some other path).
        /// </summary>
        public string Save(string name, ReadOnlySpan<byte> rgba, int width, int height)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must be non-empty.", nameof(name));
            string path = Path.Combine(OutDir, name + ".png");
            PngWriter.Save(path, rgba, width, height);
            Count++;
            _log(path);
            return path;
        }

        /// <summary>Emit the final <c>done -&gt; &lt;OutDir&gt; (N shots)</c> summary line.</summary>
        public void Done() => _log($"done -> {OutDir} ({Count} shot{(Count == 1 ? "" : "s")})");
    }
}
