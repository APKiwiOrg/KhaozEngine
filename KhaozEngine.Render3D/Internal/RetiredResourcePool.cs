using System;
using System.Collections.Generic;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>Deferred disposal for GPU resources freed mid-life (a streamed mesh unloaded while the scene keeps
    /// running). A retired resource is held for <see cref="FrameDelay"/> frame boundaries and then destroyed behind a
    /// single device drain, so a burst of unloads costs one drain on one frame instead of one drain per resource on
    /// the frame that unloaded them.
    /// <para>The rule this preserves: a GPU resource is never destroyed while queued work may still reference it
    /// (Mesa lavapipe runs submissions on its own thread and segfaults on the use-after-free, which is why the
    /// disposal sites drain at all). Nothing here weakens that. Every free still happens after a drain, and the frame
    /// delay only moves the drain to a point where the work referencing the resource is already several presented
    /// frames old, so the wait is the near-free "already idle" case rather than a mid-frame pipeline stall.</para>
    /// <para>The renderers that grow-and-retire a buffer (<c>ModelRenderer</c>, <c>ParticleRenderer</c>,
    /// <c>GroundDecalRenderer</c>, <c>OverlayMeshRenderer</c>, <c>ShadowMapRenderer</c>) keep their retired list until
    /// teardown, which is correct for a handful of geometric grows and wrong for a streaming path that retires
    /// megabytes a minute. This type is the streaming form of the same rule.</para></summary>
    internal sealed class RetiredResourcePool
    {
        /// <summary>Frame boundaries a retired resource waits before it is destroyed. Three covers the deepest
        /// CPU-ahead-of-GPU window a vsynced frame loop reaches, so by the time the drain runs the referencing work
        /// has long completed.</summary>
        public const int DefaultFrameDelay = 3;

        readonly Action _drainDevice;
        readonly List<Entry> _pending = new();

        // Frame boundaries seen. Wraps at int.MaxValue after about a year of continuous 60 Hz play. The age
        // subtraction below stays correct across the wrap because it is unchecked two's complement.
        int _frame;

        /// <summary>Build a pool over the device drain it should run before destroying anything (normally
        /// <c>IGpuDevice.WaitForIdle</c>). <paramref name="frameDelay"/> below 1 is clamped to 1, so a resource is
        /// never destroyed inside the call that retired it.</summary>
        public RetiredResourcePool(Action drainDevice, int frameDelay = DefaultFrameDelay)
        {
            _drainDevice = drainDevice ?? throw new ArgumentNullException(nameof(drainDevice));
            FrameDelay = frameDelay < 1 ? 1 : frameDelay;
        }

        /// <summary>Frame boundaries a retired resource waits before being destroyed (at least 1).</summary>
        public int FrameDelay { get; }

        /// <summary>Resources retired but not yet destroyed.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Hand a resource over to the pool. Costs nothing at the call site: no drain, no destroy. A null
        /// resource is ignored, so an optional resource (a per-mesh material set) needs no caller-side check.</summary>
        public void Retire(IDisposable? resource)
        {
            if (resource is null) return;
            _pending.Add(new Entry(resource, _frame));
        }

        /// <summary>Retire up to three related resources in one call (a mesh's vertex buffer, index buffer and
        /// optional material set), so the caller needs no null checks and no per-resource statement.</summary>
        public void Retire(IDisposable? a, IDisposable? b, IDisposable? c)
        {
            Retire(a); Retire(b); Retire(c);
        }

        /// <summary>Advance one frame and destroy everything that has waited out <see cref="FrameDelay"/>, draining
        /// once first. A frame with nothing ripe touches the device not at all.</summary>
        public void BeginFrame()
        {
            _frame++;
            int ripe = 0;
            while (ripe < _pending.Count && _frame - _pending[ripe].RetiredAt >= FrameDelay) ripe++;
            if (ripe == 0) return;
            _drainDevice();
            for (int i = 0; i < ripe; i++) _pending[i].Resource.Dispose();
            _pending.RemoveRange(0, ripe);
        }

        /// <summary>Destroy everything pending right now, draining once first. For teardown, where waiting out the
        /// frame delay would leak the tail.</summary>
        public void FlushAll()
        {
            if (_pending.Count == 0) return;
            _drainDevice();
            for (int i = 0; i < _pending.Count; i++) _pending[i].Resource.Dispose();
            _pending.Clear();
        }

        // Entries are appended in non-decreasing frame order, so the ripe ones are always a prefix of the list.
        readonly struct Entry
        {
            public readonly IDisposable Resource;
            public readonly int RetiredAt;
            public Entry(IDisposable resource, int retiredAt) { Resource = resource; RetiredAt = retiredAt; }
        }
    }
}
