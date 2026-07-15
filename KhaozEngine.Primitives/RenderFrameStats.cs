namespace KhaozEngine.Primitives
{
    /// <summary>
    /// Per-frame render cost counters: how many GPU draws a frame issued, how many instances and triangles they
    /// carried, how many bytes were streamed into GPU buffers, plus the 2D batcher's quad/flush/texture-switch
    /// tallies. A plain value type of integer fields, summed with <see cref="op_Addition"/> so a host can
    /// aggregate several surfaces' per-frame stats (e.g. the 3D scene + the 2D HUD batch) into one frame total.
    /// <para>
    /// The counters are plain increments in the submit path (no allocation, negligible cost), so the producing
    /// surfaces keep them ALWAYS ON - there is no enable flag to forget. A surface resets its own tally at the
    /// start of each frame (<see cref="Reset"/>) and exposes the finished total after the frame's draws, e.g.
    /// <c>KhaozEngine.Render2D.SpriteBatch.FrameStats</c> and <c>KhaozEngine.Render3D.Scene3D.LastFrameStats</c>.
    /// </para>
    /// <para>
    /// Triangle and instance counts are estimates from the indexed-draw sizes the submit path already knows (index
    /// count / 3 per instance): they cover the mesh geometry passes (rigid instanced, terrain splat, CPU-skinned,
    /// and the shadow-caster depth pass), not the fixed fullscreen post-process blits or the small effect/overlay
    /// quad passes, which count toward <see cref="DrawCalls"/> only. <see cref="Triangles"/> and
    /// <see cref="BufferUpdateBytes"/> are 64-bit because a busy frame's totals can exceed a 32-bit range.
    /// </para>
    /// </summary>
    public struct RenderFrameStats
    {
        /// <summary>GPU draw submissions issued this frame (each <c>Draw</c>/<c>DrawIndexed</c> record, and each
        /// scene-level effect/overlay pass submission). Post-process fullscreen blits are not itemized here.</summary>
        public int DrawCalls;

        /// <summary>Mesh instances rasterized this frame across the geometry passes (the sum of the per-run instance
        /// spans plus one per CPU-skinned draw). 0 for a pure-2D surface.</summary>
        public int Instances;

        /// <summary>Estimated triangles submitted this frame (index count / 3, times instance count), over the mesh
        /// geometry passes. Effect/overlay/post quads are excluded.</summary>
        public long Triangles;

        /// <summary>Bytes written into GPU buffers this frame (per-frame streaming: 2D vertex uploads, 3D instance +
        /// CPU-skinned vertex uploads). Static, load-time buffer fills are not counted.</summary>
        public long BufferUpdateBytes;

        /// <summary>2D only: quads emitted into the sprite batch this frame (sprites + glyphs). 0 for a 3D surface.</summary>
        public int Quads;

        /// <summary>2D only: batch flushes that issued draws this frame (each <c>End</c> and each scissor change
        /// forces one). 0 for a 3D surface.</summary>
        public int Flushes;

        /// <summary>2D only: texture bind changes across the frame's draws (a run of same-texture quads coalesces to
        /// one, so this is the count of distinct-texture transitions, &lt;= <see cref="DrawCalls"/>). 0 for a 3D surface.</summary>
        public int TextureSwitches;

        /// <summary>Clear every counter to 0 (as if freshly constructed). Called by a producing surface at frame start.</summary>
        public void Reset() => this = default;

        /// <summary>Field-wise sum of two frame tallies, for aggregating several surfaces into one frame total.</summary>
        public static RenderFrameStats operator +(in RenderFrameStats a, in RenderFrameStats b) => new()
        {
            DrawCalls = a.DrawCalls + b.DrawCalls,
            Instances = a.Instances + b.Instances,
            Triangles = a.Triangles + b.Triangles,
            BufferUpdateBytes = a.BufferUpdateBytes + b.BufferUpdateBytes,
            Quads = a.Quads + b.Quads,
            Flushes = a.Flushes + b.Flushes,
            TextureSwitches = a.TextureSwitches + b.TextureSwitches,
        };

        /// <summary>Add <paramref name="other"/> into this tally in place (same field-wise sum as <see cref="op_Addition"/>).</summary>
        public void Add(in RenderFrameStats other) => this = this + other;
    }
}
