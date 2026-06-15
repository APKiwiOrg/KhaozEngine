using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>A world-space ray: an origin and a (not necessarily normalized) direction.</summary>
    public readonly struct Ray
    {
        public Vector3 Origin { get; }
        public Vector3 Direction { get; }
        public Ray(Vector3 origin, Vector3 direction) { Origin = origin; Direction = direction; }
    }
}
