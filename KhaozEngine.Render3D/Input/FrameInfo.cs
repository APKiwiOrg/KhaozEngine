using System.Collections.Generic;

namespace KhaozEngine.Render3D
{
    /// <summary>Per-frame input + timing snapshot passed to the consumer's update callback.</summary>
    public sealed class FrameInfo
    {
        /// <summary>Seconds since the previous frame.</summary>
        public float Dt;
        /// <summary>Keys currently held.</summary>
        public IReadOnlySet<Key> Down = new HashSet<Key>();
        /// <summary>Keys that went down this frame (edge, excludes auto-repeat).</summary>
        public IReadOnlySet<Key> Pressed = new HashSet<Key>();
    }
}
