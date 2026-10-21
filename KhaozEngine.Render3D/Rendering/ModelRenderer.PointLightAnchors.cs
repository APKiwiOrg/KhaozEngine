using System.Numerics;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    /// <summary>CPU light descriptor. PosRadius is the legacy absolute approximation. Native anchors remain
    /// beside it until each receiver or point-shadow binding reduces the local position.</summary>
    public struct PointLightData
    {
        public Vector4 PosRadius;
        public Vector4 ColorIntensity;
        public LightShadow Shadow;
        internal RenderAnchor? Anchor;
        internal Vector3 LocalPosition;
        internal LightShadow LocalShadow;
        internal readonly Vector4 RelativePosRadius(Vector3 origin)
        {
            if (Anchor is not { } anchor) return PosRadius - new Vector4(origin, 0);
            return new((float)(((double)anchor.X - origin.X) + LocalPosition.X),
                (float)(((double)anchor.Y - origin.Y) + LocalPosition.Y),
                (float)(((double)anchor.Z - origin.Z) + LocalPosition.Z), PosRadius.W);
        }
        internal readonly LightShadow RelativeShadow(Vector3 origin)
        {
            if (Anchor is not { } anchor) return Shadow with
            { ExclusionMin = Shadow.ExclusionMin - origin, ExclusionMax = Shadow.ExclusionMax - origin };
            Vector3 Reduce(Vector3 p) => new((float)(((double)anchor.X - origin.X) + p.X),
                (float)(((double)anchor.Y - origin.Y) + p.Y), (float)(((double)anchor.Z - origin.Z) + p.Z));
            return LocalShadow with { ExclusionMin = Reduce(LocalShadow.ExclusionMin), ExclusionMax = Reduce(LocalShadow.ExclusionMax) };
        }
    }
}
