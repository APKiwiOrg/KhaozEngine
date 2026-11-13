using System;
using System.Collections.Generic;

namespace KhaozEngine.Render3D
{
    /// <summary>The process-wide index of live material image sources by identity, which is what makes an image
    /// shared by many parts, materials, glbs and archetypes decode once (issue #1776).
    /// <para>
    /// WEAK BY DESIGN. An entry lives exactly as long as some loaded part list still references its source, so the
    /// index adds no lifetime of its own: a world that drops its parts drops their images, and nothing here needs an
    /// eviction policy. Dead entries are swept when the table doubles.
    /// </para>
    /// <para>
    /// The decode runs outside the lock. Two threads that miss on one identity at once both decode, and the second
    /// to finish adopts the first one's source, so the result is still one shared source. That race costs a
    /// redundant decode, never a second identity.
    /// </para>
    /// </summary>
    internal static class MaterialImageCache
    {
        static readonly object Gate = new();
        static readonly Dictionary<string, WeakReference<MaterialImageSource>> Live = new(StringComparer.Ordinal);
        static int _sweepAt = 64;

        /// <summary>The live source for <paramref name="identity"/>, or a new one decoded from
        /// <paramref name="encoded"/>. Null when the image does not decode.</summary>
        internal static MaterialImageSource? Resolve(string identity, ReadOnlySpan<byte> encoded)
        {
            lock (Gate)
                if (TryLive(identity, out MaterialImageSource? live)) return live;

            MaterialImageSource? created = MaterialImageSource.TryCreate(identity, encoded);
            if (created is null) return null;

            lock (Gate)
            {
                if (TryLive(identity, out MaterialImageSource? raced)) return raced;
                Live[identity] = new WeakReference<MaterialImageSource>(created);
                if (Live.Count >= _sweepAt) Sweep();
                return created;
            }
        }

        static bool TryLive(string identity, out MaterialImageSource? source)
        {
            source = null;
            return Live.TryGetValue(identity, out WeakReference<MaterialImageSource>? weak) && weak.TryGetTarget(out source);
        }

        static void Sweep()
        {
            var dead = new List<string>();
            foreach (KeyValuePair<string, WeakReference<MaterialImageSource>> entry in Live)
                if (!entry.Value.TryGetTarget(out _)) dead.Add(entry.Key);
            foreach (string key in dead) Live.Remove(key);
            _sweepAt = Math.Max(64, Live.Count * 2);
        }
    }
}
