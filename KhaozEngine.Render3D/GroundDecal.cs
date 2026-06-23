using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>Which analytic shape a <see cref="GroundDecal"/> paints. The SDF for each is in the decal shader.</summary>
    public enum DecalShape { Circle, Ring, Beam, Cone, Arc }

    /// <summary>Blend for a ground decal (matches the decal pipeline's two variants).</summary>
    public enum DecalBlend { Alpha, Additive }

    /// <summary>
    /// One generic shaped ground decal queued for this frame: a flat shape painted onto the ground/terrain by
    /// reconstructing the surface position from the depth buffer. Presentation only; cleared each
    /// <see cref="Scene3D.Begin"/>. The higher-level telegraph wrappers (KhaozEngine.Telegraphs.Render3D) build
    /// these from a TelegraphStyle + progress.
    /// </summary>
    /// <remarks>
    /// <see cref="Size"/> packs per-shape params: Circle (x=radius); Ring (x=innerR, y=outerR);
    /// Beam (x=halfLength, y=halfWidth, oriented by <see cref="Rotation"/> about +Y from +X);
    /// Cone (x=range, y=halfAngleRad, axis from <see cref="Rotation"/>); Arc (x=radius, y=halfBandWidth,
    /// z=startAngle, w=sweepAngle). <see cref="Center"/>.Y is the ground plane height; the decal paints surfaces
    /// whose reconstructed world Y is within [Center.Y - <see cref="YTolerance"/>, Center.Y + <see cref="MaxStep"/>].
    /// </remarks>
    public struct GroundDecal
    {
        public DecalShape Shape;
        public Vector3 Center;
        public float Rotation;
        public Vector4 Size;
        public Color FillColor;
        public Color OutlineColor;
        public float EdgeThickness;
        public float FillFraction;
        public float FlashAdd;
        public DecalBlend Blend;
        public float YTolerance;
        public float MaxStep;
    }
}
