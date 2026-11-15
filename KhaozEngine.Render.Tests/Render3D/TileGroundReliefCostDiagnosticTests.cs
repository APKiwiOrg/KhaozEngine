using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// REPORT ONLY: the GPU time of the tile-ground pass for the plain program and each relief configuration, at
/// 1920x1080 with 512 px layers, read from device timestamps bracketing the pass through
/// <see cref="Scene3D.PassObserver"/>. It measures and prints, it gates nothing, which is why it is a Diagnostic
/// excluded from default CI. Two ground patterns: every tile a different mix of four materials (each fragment blends
/// up to four, the transition-heavy case) and one material everywhere (the interior of a paved square). The view is
/// orthographic and straight down, so the ground covers every pixel.
/// <para>Run it with <c>KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests -c Release --filter
/// TileGroundReliefCostDiagnosticTests</c>. On a hosted runner the same filter works through the gpu-hosted-bench
/// workflow.</para>
/// </summary>
[Trait("Category", "Diagnostic")]
public sealed class TileGroundReliefCostDiagnosticTests(ITestOutputHelper output)
{
    const int Width = 1920, Height = 1080, Layer = 512, Materials = 5;
    const int WarmupFrames = 30, MeasuredFrames = 120;

    enum Config { Plain, ReliefSetPlainMesh, ReliefMapless, ReliefHeight, ReliefFullMaps, ReliefFullMapsHex }

    [GpuFact]
    public void Report_the_tile_ground_pass_cost_per_configuration()
    {
        // Configurations are interleaved over three rounds and their samples pooled, so a drift in the machine's other
        // load lands on every configuration alike instead of on whichever ran during it.
        const int rounds = 3;
        var pass = new Dictionary<(bool, Config), List<double>>();
        var frame = new Dictionary<(bool, Config), List<double>>();
        string backend = "";
        for (int round = 0; round < rounds; round++)
            foreach (bool mixed in new[] { true, false })
                foreach (Config config in Enum.GetValues<Config>())
                {
                    (double[] p, double[] f, string b) = Measure(config, mixed);
                    backend = b;
                    if (p.Length == 0)
                    {
                        output.WriteLine($"{backend}: timestamps unsupported, nothing measured");
                        return;
                    }
                    (pass.TryGetValue((mixed, config), out var ps) ? ps : pass[(mixed, config)] = []).AddRange(p);
                    (frame.TryGetValue((mixed, config), out var fs) ? fs : frame[(mixed, config)] = []).AddRange(f);
                }
        output.WriteLine($"backend {backend}, {rounds} rounds of {MeasuredFrames} frames per configuration");
        output.WriteLine("pattern,config,median_us,p95_us,frame_median_us,albedo_mib,surface_mib");
        foreach (((bool mixed, Config config), List<double> samples) in pass)
        {
            (double albedo, double surface) = Memory(config);
            double[] p = samples.ToArray(), f = frame[(mixed, config)].ToArray();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(mixed ? "mixed" : "uniform")},{config},{Percentile(p, 0.5):F1},{Percentile(p, 0.95):F1}," +
                $"{Percentile(f, 0.5):F1},{albedo:F2},{surface:F2}"));
        }
    }

    // GPU memory of the two arrays with full mip chains (4/3 of level 0), RGBA8.
    static (double AlbedoMiB, double SurfaceMiB) Memory(Config config)
    {
        double layer = Layer * Layer * 4 * (4.0 / 3.0) / (1024 * 1024);
        int albedoLayers = config == Config.ReliefSetPlainMesh ? Materials + 1 : Materials;
        int surfaceLayers = config is Config.ReliefFullMaps or Config.ReliefFullMapsHex ? Materials
            : config == Config.ReliefSetPlainMesh ? 1 : 0;
        return (layer * albedoLayers, layer * surfaceLayers);
    }

    (double[] Pass, double[] Frame, string Backend) Measure(Config config, bool mixed)
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice gd = gpu.GpuDevice;
        string backend = gd.Backend.ToString();
        if (!gd.Capabilities.SupportsTimestamps) return ([], [], backend);
        IGpuResourceFactory factory = gd.Factory;
        using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(Width, Height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, target);
        using var scene = new Scene3D(gd, framebuffer.Outputs);
        using IGpuCommandList commands = factory.CreateCommandList();
        using IGpuTimestampPool pool = factory.CreateTimestampPool(4);
        using IGpuFence fence = factory.CreateFence();
        var observer = new PassMarkers(pool);
        scene.PassObserver = observer;

        bool surface = config is Config.ReliefFullMaps or Config.ReliefFullMapsHex or Config.ReliefSetPlainMesh;
        // ReliefSetPlainMesh adds a sixth layer with every map and hex-tiling that the ground never names, so the set is
        // a relief set and the mesh still draws with the plain program.
        int count = config == Config.ReliefSetPlainMesh ? Materials + 1 : Materials;
        var layers = new TileGroundLayerImage[count];
        for (int i = 0; i < count; i++)
        {
            bool maps = config == Config.ReliefSetPlainMesh ? i == Materials : config >= Config.ReliefHeight;
            bool surfaceMaps = surface && (config != Config.ReliefSetPlainMesh || i == Materials);
            layers[i] = new TileGroundLayerImage
            {
                AlbedoRgba = Noise(Layer, 11 + i, maps),
                TilesPerMetre = 0.55f,
                HeightMapped = maps,
                SurfaceRgba = surfaceMaps ? SurfaceNoise(Layer, 31 + i) : null,
                NormalMapped = surfaceMaps,
                RoughnessMapped = surfaceMaps,
                FeatherBreakup = config == Config.ReliefMapless ? 0.5f : 0f,
                AntiTiling = config == Config.ReliefFullMapsHex || (config == Config.ReliefSetPlainMesh && i == Materials)
                    ? TileGroundAntiTiling.Offset : TileGroundAntiTiling.None,
            };
        }
        var material = scene.LoadTileGroundMaterial(Layer, Layer, layers, surface ? Layer : 0, surface ? Layer : 0);
        MeshHandle ground = scene.LoadMesh(Tiles(mixed), material);
        scene.Post.Outline = false;
        scene.Camera.Azimuth = 0f;
        scene.Camera.Elevation = 1.5f;
        scene.Camera.Target = Vector3.Zero;
        scene.Camera.AspectRatio = (float)Width / Height;
        scene.Camera.Zoom = 1f;
        scene.Camera.OrthoSize = 27f;

        var pass = new List<double>();
        var frame = new List<double>();
        Span<ulong> ticks = stackalloc ulong[4];
        try
        {
            for (int f = 0; f < WarmupFrames + MeasuredFrames; f++)
            {
                scene.Begin();
                scene.Draw(ground, Matrix4x4.Identity);
                scene.PrepareFrame(Width, Height);
                using (GpuRecording.Open(gd, commands, "TileGroundReliefCost"))
                {
                    commands.BeginTimestamps(pool, 4);
                    commands.WriteTimestamp(pool, 0);
                    scene.RenderInternal(commands, Width, Height, framebuffer);
                    commands.WriteTimestamp(pool, 3);
                    commands.EndTimestamps(pool);
                }
                gd.Submit(commands, fence);
                gd.WaitForIdle();
                GpuTimestampStatus status = pool.TryResolve(fence, ticks, out double period);
                // The seam refuses a fence still armed from the last submission.
                fence.Reset();
                if (status != GpuTimestampStatus.Ready || !observer.Bracketed) continue;
                if (f < WarmupFrames) continue;
                pass.Add((ticks[2] - ticks[1]) * period / 1000.0);
                frame.Add((ticks[3] - ticks[0]) * period / 1000.0);
                observer.Bracketed = false;
            }
        }
        finally
        {
            gd.WaitForIdle();
        }
        return (pass.ToArray(), frame.ToArray(), backend);
    }

    // Markers 1 and 2 around the tile-ground pass, inside the batch the frame loop opens around the whole render.
    sealed class PassMarkers(IGpuTimestampPool pool) : IRenderPassObserver
    {
        internal bool Bracketed;
        public void BeginPass(IGpuCommandList commands, RenderPassKey key)
        {
            if (key.Id == "opaque.tile-ground") commands.WriteTimestamp(pool, 1);
        }
        public void EndPass(IGpuCommandList commands, RenderPassKey key)
        {
            if (key.Id != "opaque.tile-ground") return;
            commands.WriteTimestamp(pool, 2);
            Bracketed = true;
        }
        public void SkipPass(IGpuCommandList commands, RenderPassKey key) { }
        public void BeginCpuPhase(string id) { }
        public void EndCpuPhase(string id) { }
    }

    // A 48 by 27 grid of one metre tiles, per-triangle vertices as the mesher emits them: four corner slots per tile
    // with one-hot corner weights, so every fragment blends its tile's four corner materials.
    static GltfMesh Tiles(bool mixed)
    {
        const int across = 48, down = 27;
        var verts = new List<ModelVertex>();
        var indices = new List<uint>();
        var rng = new Random(7);
        for (int z = 0; z < down; z++)
            for (int x = 0; x < across; x++)
            {
                float x0 = x - across / 2f, z0 = z - down / 2f;
                int[] s = mixed
                    ? new[] { rng.Next(Materials), rng.Next(Materials), rng.Next(Materials), rng.Next(Materials) }
                    : new[] { 0, 0, 0, 0 };
                var uv = new Vector2(s[0], s[1]);
                var tangent = new Vector4(s[2], s[3], 1f, 0f);
                ModelVertex V(float px, float pz, int corner) => new(new Vector3(px, 0f, pz), Vector3.UnitY,
                    corner switch { 0 => new Vector4(1, 0, 0, 0), 1 => new Vector4(0, 1, 0, 0), 2 => new Vector4(0, 0, 1, 0), _ => new Vector4(0, 0, 0, 1) },
                    uv, tangent);
                void Tri(ModelVertex a, ModelVertex b, ModelVertex c)
                {
                    uint first = (uint)verts.Count;
                    verts.Add(a); verts.Add(b); verts.Add(c);
                    indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
                }
                ModelVertex sw = V(x0, z0 + 1, 0), se = V(x0 + 1, z0 + 1, 1), nw = V(x0, z0, 2), ne = V(x0 + 1, z0, 3);
                Tri(sw, se, ne);
                Tri(sw, ne, nw);
            }
        return new GltfMesh(verts.ToArray(), indices.ToArray());
    }

    static byte[] Noise(int size, int seed, bool height)
    {
        var rng = new Random(seed);
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            byte v = (byte)rng.Next(60, 200);
            px[i] = v; px[i + 1] = (byte)(v * 0.9f); px[i + 2] = (byte)(v * 0.7f);
            px[i + 3] = height ? (byte)rng.Next(256) : (byte)255;
        }
        return px;
    }

    static byte[] SurfaceNoise(int size, int seed)
    {
        var rng = new Random(seed);
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)rng.Next(100, 156); px[i + 1] = (byte)rng.Next(100, 156); px[i + 2] = 240;
            px[i + 3] = (byte)rng.Next(256);
        }
        return px;
    }

    static double Percentile(double[] values, double p)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }
}
