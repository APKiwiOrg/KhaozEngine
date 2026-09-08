using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Native GPU coverage for the authored prop LOD handoff. LOD0 and LOD1 use the same panel geometry
    /// with visibly different vertex colors, so the midpoint must preserve the solid silhouette without producing
    /// pixels brighter than either endpoint. The same midpoint must preserve the caster in the shadow atlas.</summary>
    public sealed class PropLodCrossfadeGpuTests
    {
        const int W = 192;
        const int H = 192;
        readonly ITestOutputHelper _output;

        public PropLodCrossfadeGpuTests(ITestOutputHelper output) => _output = output;

        [GpuFact]
        public void ComplementaryLodHalvesPreserveColorAndShadowCoverageAtTheMidpoint()
        {
            using GpuDeviceContext context = GpuDeviceContext.CreateHeadless();
            using var preview = new Render3DPreview(context.GpuDevice, W, H, new ShadowSettings
            {
                Mode = ShadowMode.ShadowMap,
                ShadowCascadeCount = 4,
                ShadowNearDistance = 16f,
                ShadowMaxDistance = 250f,
            });

            Scene3D scene = preview.Scene;
            MeshHandle full = scene.LoadMesh(Colored(MeshPrimitives.Tile(3f, 0.05f), new Vector4(1f, 0.1f, 0.1f, 1f)));
            MeshHandle lod = scene.LoadMesh(Colored(MeshPrimitives.Tile(3f, 0.05f), new Vector4(0.1f, 0.2f, 1f, 1f)));
            var placements = new[] { new PropPlacement("panel", 0f, 6f, 0f, 1f, 0f, 0) };
            var fullMeshes = new Dictionary<string, MeshHandle> { ["panel"] = full };
            var lodMeshes = new Dictionary<string, MeshHandle> { ["panel"] = lod };
            PropLayer layer = PropLayer.PlacementLayer(placements, fullMeshes, 400f,
                lodMeshes: lodMeshes, lodDistance: 64f).WithLodCrossfade(16f);
            using var renderer = new PropClusterRenderer(scene);
            PropClusterKey key = new("panel", 0, 0, 0);
            renderer.Apply(key, renderer.BuildCpu(new PropClusterBuildRequest(
                key, 1, new RectArea(-2f, -2f, 2f, 2f), layer, placements)));

            scene.Post.AmbientColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = Color.Black;
            scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.15f, -0.97f, 0.2f));
            scene.Camera.Frame(new Vector3(0f, 6f, 0f), new Vector3(5f, 5f, 5f));

            byte[] near = Capture(55.99f);
            int solidShadow = ShadowTexels(scene.DebugReadShadowMap(out _, out _));
            byte[] midpoint = Capture(64f);
            int midpointShadow = ShadowTexels(scene.DebugReadShadowMap(out _, out _));
            byte[] far = Capture(72f);

            int nearCoverage = CoveredPixels(near);
            int midpointCoverage = CoveredPixels(midpoint);
            int farCoverage = CoveredPixels(far);
            int midpointRed = RedPixels(midpoint);
            int midpointBlue = BluePixels(midpoint);
            _output.WriteLine($"color coverage near={nearCoverage}, midpoint={midpointCoverage}, far={farCoverage}");
            _output.WriteLine($"midpoint ownership red={midpointRed}, blue={midpointBlue}");
            _output.WriteLine($"shadow coverage solid={solidShadow}, midpoint={midpointShadow}");

            Assert.True(RedPixels(near) > 500, "LOD0 is not visibly red at the near endpoint");
            Assert.True(BluePixels(far) > 500, "LOD1 is not visibly blue at the far endpoint");
            Assert.InRange(midpointCoverage, (int)(nearCoverage * 0.95f), (int)(nearCoverage * 1.05f));
            Assert.InRange(midpointCoverage, (int)(farCoverage * 0.95f), (int)(farCoverage * 1.05f));
            Assert.True(midpointRed >= midpointCoverage * 0.2f,
                $"LOD0 owns only {midpointRed} of {midpointCoverage} midpoint pixels");
            Assert.True(midpointBlue >= midpointCoverage * 0.2f,
                $"LOD1 owns only {midpointBlue} of {midpointCoverage} midpoint pixels");
            AssertWithinEndpointBrightness(near, midpoint, far);
            Assert.True(solidShadow > 1000, $"the solid panel records only {solidShadow} shadow texels");
            Assert.True(midpointShadow >= solidShadow * 0.95f,
                $"the midpoint records {midpointShadow} of {solidShadow} solid shadow texels");

            byte[] Capture(float focusDistance)
            {
                preview.Capture(_ => renderer.Draw(new Vector3(focusDistance, 0f, 0f)));
                return preview.ReadbackRgba();
            }
        }

        static GltfMesh Colored(GltfMesh source, Vector4 color)
        {
            var vertices = new ModelVertex[source.Vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = source.Vertices[i];
                vertices[i].Color = color;
            }
            return new GltfMesh(vertices, source.Indices32);
        }

        static int CoveredPixels(byte[] rgba)
        {
            int count = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i] > 30 || rgba[i + 1] > 30 || rgba[i + 2] > 30) count++;
            return count;
        }

        static int RedPixels(byte[] rgba)
        {
            int count = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i] > rgba[i + 2] * 2) count++;
            return count;
        }

        static int BluePixels(byte[] rgba)
        {
            int count = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i + 2] > rgba[i] * 2) count++;
            return count;
        }

        static void AssertWithinEndpointBrightness(byte[] near, byte[] midpoint, byte[] far)
        {
            int overshoot = 0;
            for (int i = 0; i < midpoint.Length; i += 4)
            {
                int mid = midpoint[i] + midpoint[i + 1] + midpoint[i + 2];
                int nearLimit = near[i] + near[i + 1] + near[i + 2];
                int farLimit = far[i] + far[i + 1] + far[i + 2];
                if (mid > System.Math.Max(nearLimit, farLimit) + 9) overshoot++;
            }
            Assert.True(overshoot <= 4, $"the midpoint has {overshoot} pixels brighter than both endpoints");
        }

        static int ShadowTexels(float[] depth)
        {
            int count = 0;
            for (int i = 0; i < depth.Length; i++) if (depth[i] < 0.999f) count++;
            return count;
        }
    }
}
