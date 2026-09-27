using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// An <see cref="ICameraBoomProbe"/> that reports a free length the test sets, and records how it was asked,
    /// so a row can obstruct the boom and check the query without building a world.
    /// </summary>
    internal sealed class FixedReachProbe : ICameraBoomProbe
    {
        /// <summary>Free length to report, capped at the asked length. Null reports the full length, a clear boom.
        /// Assigning it is this fake's "an obstruction moved", which no camera field can see.</summary>
        public float? ReachAt;

        /// <summary>Calls made through <see cref="Reach"/>, counted on the probe's own side.</summary>
        public int Calls;

        public Vector3 LastOrigin;
        public Vector3 LastDirection;
        public float LastLength;
        public float LastRadius;

        public float Reach(Vector3 origin, Vector3 direction, float length, float radius)
        {
            Calls++;
            LastOrigin = origin;
            LastDirection = direction;
            LastLength = length;
            LastRadius = radius;
            return ReachAt is { } reach ? MathF.Min(reach, length) : length;
        }
    }
}
