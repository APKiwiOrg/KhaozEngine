using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Telegraphs
{
    /// <summary>
    /// Ground-plane telegraph extensions on <see cref="Scene3D"/>. Each method resolves the style at the given
    /// 0..1 progress and queues a <see cref="GroundDecal"/> via the engine's generic depth-sampling decal pass.
    /// Immediate-mode; presentation only. The Build* statics are the pure mapping (headless-testable); the
    /// extension methods are thin wrappers.
    /// </summary>
    public static class GroundTelegraphs
    {
        // Default terrain gate: paint a little below the ground plane and up one small step; tweak per call if a
        // game has tall terrain features inside a zone.
        const float DefaultYTolerance = 0.3f;
        const float DefaultMaxStep = 0.5f;

        static DecalBlend Blend(TelegraphBlend b) => b == TelegraphBlend.Additive ? DecalBlend.Additive : DecalBlend.Alpha;

        static GroundDecal Base(DecalShape shape, Vector3 center, float rotation, Vector4 size, in ResolvedTelegraph r) => new()
        {
            Shape = shape,
            Center = center,
            Rotation = rotation,
            Size = size,
            FillColor = r.FillColor,
            OutlineColor = r.OutlineColor,
            EdgeThickness = r.EdgeThickness,
            FillFraction = r.FillFraction,
            FlashAdd = r.FlashAdd,
            Blend = Blend(r.Blend),
            YTolerance = DefaultYTolerance,
            MaxStep = DefaultMaxStep,
        };

        static float RotFromXZ(Vector2 dirXZ) =>
            dirXZ.LengthSquared() > 1e-6f ? MathF.Atan2(dirXZ.Y, dirXZ.X) : 0f;

        public static GroundDecal BuildCircle(Vector3 center, float radius, float progress, in TelegraphStyle style) =>
            Base(DecalShape.Circle, center, 0f, new Vector4(radius, 0, 0, 0), TelegraphResolve.Resolve(progress, style));

        public static GroundDecal BuildRing(Vector3 center, float inner, float outer, float progress, in TelegraphStyle style) =>
            Base(DecalShape.Ring, center, 0f, new Vector4(inner, outer, 0, 0), TelegraphResolve.Resolve(progress, style));

        public static GroundDecal BuildBeam(Vector3 origin, Vector2 dirXZ, float length, float width, float progress, in TelegraphStyle style)
        {
            // Decal center is the beam midpoint (the box SDF in the shader is origin-at-one-end, so the renderer
            // anchors at Center; place Center at the origin and let the SDF extend along +x by halfLength*2). To
            // keep the shader's "origin at one end" assumption, Center = origin and Size.x = halfLength = length/2,
            // Size.y = halfWidth.
            var r = TelegraphResolve.Resolve(progress, style);
            return Base(DecalShape.Beam, origin, RotFromXZ(dirXZ), new Vector4(length * 0.5f, width * 0.5f, 0, 0), r);
        }

        public static GroundDecal BuildCone(Vector3 origin, Vector2 dirXZ, float halfAngleRad, float range, float progress, in TelegraphStyle style) =>
            Base(DecalShape.Cone, origin, RotFromXZ(dirXZ), new Vector4(range, halfAngleRad, 0, 0), TelegraphResolve.Resolve(progress, style));

        public static GroundDecal BuildArc(Vector3 center, float radius, float bandWidth, float startAngle, float sweepAngle, float progress, in TelegraphStyle style) =>
            Base(DecalShape.Arc, center, 0f, new Vector4(radius, bandWidth * 0.5f, startAngle, sweepAngle), TelegraphResolve.Resolve(progress, style));

        // ---- Thin Scene3D extension wrappers ----
        public static void GroundCircle(this Scene3D scene, Vector3 center, float radius, float progress, in TelegraphStyle style) =>
            scene.DrawGroundDecal(BuildCircle(center, radius, progress, style));

        public static void GroundRing(this Scene3D scene, Vector3 center, float inner, float outer, float progress, in TelegraphStyle style) =>
            scene.DrawGroundDecal(BuildRing(center, inner, outer, progress, style));

        public static void GroundBeam(this Scene3D scene, Vector3 origin, Vector2 dirXZ, float length, float width, float progress, in TelegraphStyle style) =>
            scene.DrawGroundDecal(BuildBeam(origin, dirXZ, length, width, progress, style));

        public static void GroundCone(this Scene3D scene, Vector3 origin, Vector2 dirXZ, float halfAngleRad, float range, float progress, in TelegraphStyle style) =>
            scene.DrawGroundDecal(BuildCone(origin, dirXZ, halfAngleRad, range, progress, style));

        public static void GroundArc(this Scene3D scene, Vector3 center, float radius, float bandWidth, float startAngle, float sweepAngle, float progress, in TelegraphStyle style) =>
            scene.DrawGroundDecal(BuildArc(center, radius, bandWidth, startAngle, sweepAngle, progress, style));
    }
}
