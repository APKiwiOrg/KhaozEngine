using System.Collections.Generic;

namespace KhaozEngine.Render2D
{
    /// <summary>Engine-native key identifiers (no Veldrid types leak out). POC: shared windowing/input will
    /// move to a dedicated platform package later (see docs/ROADMAP.md).</summary>
    public enum Key
    {
        Escape, Space, Q, W, E, R, A, S, D, O, C, P,
        Up, Down, Left, Right,
        Number1, Number2, Number3, Number4, Number5,
    }

    /// <summary>Per-frame input + timing snapshot.</summary>
    public sealed class FrameInfo
    {
        public float Dt;
        public int Width, Height;
        public IReadOnlySet<Key> Down = new HashSet<Key>();
        public IReadOnlySet<Key> Pressed = new HashSet<Key>();
    }
}
