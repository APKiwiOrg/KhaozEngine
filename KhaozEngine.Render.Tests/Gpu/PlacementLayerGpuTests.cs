using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of a placement layer (issue #286) drawn through the REAL <see cref="Scene3DChunkSink.Load"/> +
    /// <see cref="Scene3DChunkSink.Draw"/> wire, not a bare <c>PropRenderer.Queue</c> call. Pixel-presence only,
    /// no baseline image (so no new bake and no cross-platform gate): one hand-placed prop renders as visible pixels, and the
    /// same scene with an empty placement list renders none. This proves the whole runtime path - a frozen
    /// placement list, through the sink's per-chunk bucket, to the GPU - actually lands a placement layer on
    /// screen. The knobs (fade band, LOD, HLOD, collider gating) are exercised headlessly in
    /// <c>PlacementLayerTests</c> / <c>PropRendererTests</c>. This only proves the wire carries pixels. Skipped
    /// unless KE_GPU_TESTS is set (see <see cref="GpuFactAttribute"/>).
    /// <para>The terrain chunk the sink also draws sits far below the origin (<see cref="FarBelowField"/>), while
    /// the camera is framed tightly on the origin prop exactly like <c>PropFadeBandGpuTests</c>. With an
    /// orthographic camera that keeps the terrain plane's depth far past the far clip plane regardless of where in
    /// the chunk it falls, so it never reaches the viewport - the captured frame is either the prop or pure
    /// background, never a terrain silhouette muddying the read.</para>
    /// </summary>
    public sealed class PlacementLayerGpuTests
    {
        const int W = 128, H = 128;
        const float ChunkSize = 20f;

        // A flat meadow WAY below the origin: with the camera framed tightly on the origin prop (see
        // CoveredPixelsFor), the terrain plane's depth clears the far clip plane by a wide margin no matter where in
        // the chunk it falls, so a loaded-and-drawn terrain chunk still never reaches the viewport.
        static TerrainField FarBelowField() => new TerrainField(new TerrainConfig
        {
            GentleAmplitude = 0f,
            WaterLevel = 0f,
            Biomes = new[]
            {
                new BiomeBand
                {
                    Start = float.NegativeInfinity, End = float.PositiveInfinity,
                    Biome = BiomeId.Meadow, BaseHeight = -1000f, HillAmplitude = 0f,
                },
            },
        });

        // A pixel is "covered" (prop, not background) when any channel clears a small floor - the box is bright
        // grey on a black background. Mirrors PropFadeBandGpuTests.CoveredPixels.
        static int CoveredPixels(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i] > 40 || rgba[i + 1] > 40 || rgba[i + 2] > 40) n++;
            return n;
        }

        // Load a one-layer placement sink at chunk (0, 0) and draw it through the real sink wire: Load builds +
        // uploads the CPU build, Draw queues the terrain chunk plus every in-range prop for the frame.
        static int CoveredPixelsFor(IReadOnlyList<PropPlacement> placements)
        {
            Scene3DChunkSink sink = null!;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
                    scene.Post.AmbientColor = new Color(0.7f, 0.7f, 0.7f, 1f);
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
                    scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));

                    var meshes = new Dictionary<string, MeshHandle> { ["box"] = box };
                    PropLayer layer = PropLayer.PlacementLayer(placements, meshes, drawRadius: 50f);
                    sink = new Scene3DChunkSink(scene, FarBelowField(), new[] { layer }, chunkSize: ChunkSize);
                    sink.Load(new ChunkCoord(0, 0), lod: 0);
                },
                drawFrame: scene => sink.Draw(Vector3.Zero),
                frames: 1);
            return CoveredPixels(rgba);
        }

        [GpuFact]
        public void Placement_layer_prop_renders_visible_pixels_through_the_sink()
        {
            var placements = new List<PropPlacement> { new PropPlacement("box", 0f, 0f, 0f, 1f, 0f, 0) };

            int covered = CoveredPixelsFor(placements);

            Assert.True(covered > 0,
                "a placement layer's prop must render visible pixels through Scene3DChunkSink.Load + Draw");
        }

        [GpuFact]
        public void Placement_layer_empty_placements_renders_only_background()
        {
            int covered = CoveredPixelsFor(Array.Empty<PropPlacement>());

            Assert.Equal(0, covered);
        }
    }
}
