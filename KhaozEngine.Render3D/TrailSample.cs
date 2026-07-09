using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// One resolved sample of a motion trail for <see cref="Scene3D.DrawTrail"/>: a world <see cref="Position"/>,
    /// the ribbon half-width across it at this point (<see cref="HalfWidth"/>, world units), and the strip
    /// <see cref="Alpha"/> [0,1] here (multiplies the <see cref="TrailStyle"/> colour, so a fading tail is just
    /// decreasing alpha toward the oldest sample). Optional <see cref="Facing"/> twists the ribbon onto a fixed
    /// plane; leave it zero for a camera-facing strip (the common case). Samples are fed oldest-first (tail to head).
    /// </summary>
    public readonly record struct TrailSample(Vector3 Position, float HalfWidth, float Alpha)
    {
        /// <summary>Optional per-sample facing. When non-zero, the ribbon's across-direction here is perpendicular to
        /// both this vector and the local tangent (twist-following: the strip holds a fixed plane regardless of the
        /// camera, e.g. a blade's sweep plane). Zero (default) =&gt; camera-facing at this sample.</summary>
        public Vector3 Facing { get; init; }
    }
}
