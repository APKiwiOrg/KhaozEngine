using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A celestial body <see cref="SunCycle"/> wants drawn besides the one holding the primary disc slot: where it
    /// is and what colour it is (alpha is its horizon fade). The cycle does not own disc SHAPE, so
    /// <see cref="SunCycle.Apply"/> gives it the primary disc's radius and halo when it writes it to
    /// <see cref="SkySettings.ExtraDiscs"/>, which is also what makes a body moving between the primary slot and the
    /// extras invisible.
    /// </summary>
    public readonly struct SunCycleDisc
    {
        /// <summary>Create a disc pointing at <paramref name="direction"/> in <paramref name="color"/>.</summary>
        public SunCycleDisc(Vector3 direction, Color color)
        {
            Direction = direction;
            Color = color;
        }

        /// <summary>World-space unit direction TO the body.</summary>
        public Vector3 Direction { get; }

        /// <summary>Disc color. Alpha carries the body's horizon fade.</summary>
        public Color Color { get; }
    }
}
