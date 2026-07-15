using System;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    // Device-free CPU-cost micro-harness contrasting the two skinning paths' per-frame CPU work for a crowd of N
    // same-mesh characters (the MMO case the feature targets):
    //   CPU path  = compose the palette + run SkinningMath.SkinVertex over EVERY vertex (the deform), each frame.
    //   GPU path  = compose the palette + pack the per-draw combined slot (Mvp fold + a bone-palette memcpy); the
    //               GPU does the per-vertex deform, so the CPU touches O(bones), not O(vertices).
    // Both paths share the bone-palette compose, so the delta is the per-vertex deform (CPU-only) vs the palette
    // pack (GPU). For a real character mesh (vertices >> bones) the GPU path's CPU cost is a small fraction. A plain
    // [Fact] (no GPU) so it runs in the fast loop; the numbers print to the test output.
    public sealed class GpuSkinningPerfTests
    {
        readonly ITestOutputHelper _out;
        public GpuSkinningPerfTests(ITestOutputHelper o) => _out = o;

        const int Bones = 6;
        const int Frames = 40;                 // per-N frames timed (the crowd is re-skinned every frame)
        static readonly int[] Ns = { 1, 16, 64 };

        [Fact]
        public void CpuSkinLoop_VsGpuPalettePack_Cost()
        {
            // A dense character-ish mesh: vertices >> bones (the regime GPU skinning wins). 41x40 rings = 1640 verts.
            var mesh = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 40, 40, Bones, Axis.Z);
            SkinnedVertex[] src = mesh.Vertices;

            // One composed palette (inverseBind * jointWorld), shared by both timed paths (they both compose it).
            var palette = new Matrix4x4[Bones];
            for (int b = 0; b < Bones; b++) palette[b] = SkinningMath.Compose(mesh.RestPose[b], mesh.InverseBind[b]);

            Matrix4x4 vp = Matrix4x4.CreateLookAt(new Vector3(4, 4, 5), new Vector3(0, 0, 2), Vector3.UnitY)
                         * Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1f, 0.1f, 100f);
            Matrix4x4 model = Matrix4x4.CreateTranslation(1f, 0f, 0f);

            var cpuDst = new ModelVertex[src.Length];        // reused deform target (per character)
            var packScratch = new Matrix4x4[3 + Bones];      // Mvp/Model/P + bones (mirrors PackSkinnedMainSlot)

            // Warm up the JIT for both paths.
            CpuDeform(src, palette, cpuDst, 4);
            GpuPack(palette, packScratch, model, vp, 4);

            _out.WriteLine($"vertices={src.Length}, bones={Bones}, frames/measure={Frames}");
            _out.WriteLine("N     CPU skin-loop ms/frame    GPU pack ms/frame    speedup");
            double lastCpu = 0, lastGpu = 0;
            foreach (int n in Ns)
            {
                double cpuMs = TimeMs(() => { for (int f = 0; f < Frames; f++) CpuDeform(src, palette, cpuDst, n); }) / Frames;
                double gpuMs = TimeMs(() => { for (int f = 0; f < Frames; f++) GpuPack(palette, packScratch, model, vp, n); }) / Frames;
                _out.WriteLine($"{n,-4}  {cpuMs,20:0.0000}    {gpuMs,17:0.0000}    {(gpuMs > 0 ? cpuMs / gpuMs : 0),6:0.0}x");
                lastCpu = cpuMs; lastGpu = gpuMs;
            }

            // At crowd scale with vertices >> bones, the GPU path's CPU-side cost is strictly the cheaper of the two.
            Assert.True(lastGpu < lastCpu,
                $"expected the GPU palette-pack CPU cost ({lastGpu:0.0000} ms) below the CPU skin-loop cost ({lastCpu:0.0000} ms) at N={Ns[^1]}");
        }

        // CPU path per-frame: compose (shared) + SkinVertex over every vertex, for n characters.
        static void CpuDeform(SkinnedVertex[] src, Matrix4x4[] palette, ModelVertex[] dst, int n)
        {
            for (int c = 0; c < n; c++)
                for (int v = 0; v < src.Length; v++)
                    dst[v] = SkinningMath.SkinVertex(src[v], palette);
        }

        // GPU path per-frame CPU work: fold Mvp + copy the palette into the combined slot scratch, for n characters
        // (the GPU shader does the per-vertex deform, so the CPU never touches a vertex).
        static void GpuPack(Matrix4x4[] palette, Matrix4x4[] scratch, in Matrix4x4 model, in Matrix4x4 vp, int n)
        {
            for (int c = 0; c < n; c++)
            {
                scratch[0] = model * vp;   // Mvp fold
                scratch[1] = model;
                scratch[2] = Matrix4x4.Identity;   // packed Tint/Emissive/SpecParams
                for (int b = 0; b < palette.Length; b++) scratch[3 + b] = palette[b];
            }
        }

        static double TimeMs(Action a)
        {
            var sw = Stopwatch.StartNew();
            a();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
    }
}
