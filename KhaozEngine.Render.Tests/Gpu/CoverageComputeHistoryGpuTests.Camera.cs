using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageComputeHistoryGpuTests
{
    sealed class HistoryCamera(float distance = 4, float width = 8) : IIsoCamera3D, IRenderOriginAware
    {
        public Vector3 RenderOrigin { get; set; }
        internal Matrix4x4? ProjectionOverride;
        public Matrix4x4 View => Matrix4x4.CreateTranslation(RenderOrigin + new Vector3(0, 0, -distance));
        public Matrix4x4 Projection => ProjectionOverride ?? Matrix4x4.CreateOrthographic(width, width, .1f, 100);
        public Matrix4x4 ViewProjection => View * Projection;
        public Matrix4x4 AbsoluteViewProjection => Matrix4x4.CreateTranslation(-RenderOrigin) * ViewProjection;
        public Vector3 Eye => new(0, 0, distance);
        public Vector3 Forward => -Vector3.UnitZ;
        public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 screenPixel)
        { screenPixel = default; return false; }
    }
}
