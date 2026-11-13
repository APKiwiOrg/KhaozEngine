using System;
using System.Collections.Generic;
using System.Threading;

namespace KhaozEngine.Render3D
{
    /// <summary>Decodes one material image into a source, or answers null when the image does not decode.</summary>
    internal delegate MaterialImageSource? MaterialImageDecode(string identity, ReadOnlySpan<byte> encoded);

    /// <summary>The process-wide index of live material image sources by identity, which is what makes an image
    /// shared by many parts, materials, glbs and archetypes decode once (issue #1776).
    /// <para>
    /// WEAK BY DESIGN. An entry lives exactly as long as some loaded part list still references its source, so the
    /// index adds no lifetime of its own: a world that drops its parts drops their images, and nothing here needs an
    /// eviction policy. Dead entries are swept when the table doubles.
    /// </para>
    /// <para>
    /// ONE DECODE PER IDENTITY, EVEN UNDER CONCURRENT LOADS. A miss registers its identity as in flight under the
    /// lock and decodes outside it. Another miss on the same identity joins that decode and waits for its source
    /// instead of decoding again, so worker threads loading several glbs that share one image decode it once. A miss
    /// on any other identity never waits for it. A failed decode reaches the loads that joined it and is not
    /// remembered, so the next load of that identity decodes again.
    /// </para>
    /// </summary>
    internal sealed class MaterialImageCache
    {
        readonly object _gate = new();
        readonly Dictionary<string, WeakReference<MaterialImageSource>> _live = new(StringComparer.Ordinal);
        readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);
        readonly MaterialImageDecode _decode;
        readonly Action<string>? _joined;
        int _sweepAt = 64;

        /// <param name="decode">The decode a miss runs.</param>
        /// <param name="joined">Called with the identity when a miss joins a decode already in flight, before it
        /// waits. A test seam, null in the shared cache.</param>
        internal MaterialImageCache(MaterialImageDecode decode, Action<string>? joined = null)
        {
            _decode = decode;
            _joined = joined;
        }

        /// <summary>The cache every glTF load shares.</summary>
        internal static MaterialImageCache Shared { get; } = new(MaterialImageSource.TryCreate);

        /// <summary>The live source for <paramref name="identity"/>, the source a decode already in flight produces,
        /// or a new one decoded from <paramref name="encoded"/>. Null when the image does not decode.</summary>
        internal MaterialImageSource? Resolve(string identity, ReadOnlySpan<byte> encoded)
        {
            InFlight? joined, owned = null;
            lock (_gate)
            {
                if (TryLive(identity, out MaterialImageSource? live)) return live;
                if (!_inFlight.TryGetValue(identity, out joined))
                    _inFlight.Add(identity, owned = new InFlight());
            }

            if (joined is not null)
            {
                _joined?.Invoke(identity);
                return joined.Wait();
            }

            MaterialImageSource? created = null;
            try
            {
                created = _decode(identity, encoded);
                return created;
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight.Remove(identity);
                    if (created is not null)
                    {
                        _live[identity] = new WeakReference<MaterialImageSource>(created);
                        if (_live.Count >= _sweepAt) Sweep();
                    }
                }
                owned!.Complete(created);
            }
        }

        bool TryLive(string identity, out MaterialImageSource? source)
        {
            source = null;
            return _live.TryGetValue(identity, out WeakReference<MaterialImageSource>? weak) && weak.TryGetTarget(out source);
        }

        void Sweep()
        {
            var dead = new List<string>();
            foreach (KeyValuePair<string, WeakReference<MaterialImageSource>> entry in _live)
                if (!entry.Value.TryGetTarget(out _)) dead.Add(entry.Key);
            foreach (string key in dead) _live.Remove(key);
            _sweepAt = Math.Max(64, _live.Count * 2);
        }

        /// <summary>One decode in flight: the source it produced, or null when it failed, once it completes.</summary>
        sealed class InFlight
        {
            readonly object _gate = new();
            MaterialImageSource? _source;
            bool _done;

            internal void Complete(MaterialImageSource? source)
            {
                lock (_gate)
                {
                    _source = source;
                    _done = true;
                    Monitor.PulseAll(_gate);
                }
            }

            internal MaterialImageSource? Wait()
            {
                lock (_gate)
                {
                    while (!_done) Monitor.Wait(_gate);
                    return _source;
                }
            }
        }
    }
}
