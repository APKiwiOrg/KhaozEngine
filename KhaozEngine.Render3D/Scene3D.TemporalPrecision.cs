using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>The precision of the scene's temporal programs (<see cref="TemporalResolvePrecisionPolicy"/>).</summary>
    public sealed partial class Scene3D
    {
        /// <summary>The precision this scene's resolve builds its programs at, or null for the policy. Read when the
        /// resolve is first built. For tests, such as the cost measurement, which times both.</summary>
        internal TemporalResolvePrecision? TemporalResolvePrecisionForTests { get; set; }
    }
}
