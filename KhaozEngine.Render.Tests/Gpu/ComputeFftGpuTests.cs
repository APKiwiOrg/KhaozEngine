using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Proof test (c) for the compute seam: a 2D radix-2 Stockham FFT on the GPU, checked three ways.
    ///
    /// This one is deliberate rather than incidental. The FFT ocean program that consumes this seam is built on
    /// exactly this kernel, so validating it here means the algorithm is proved on Metal, Direct3D11 and Vulkan
    /// before any ocean code exists, and a later failure can be attributed to the ocean rather than to the transform
    /// or the compute plumbing under it. <see cref="ComputeShaders.FftStage"/> is the seed pattern.
    ///
    /// The three checks build on each other:
    /// <list type="number">
    ///   <item>A CPU reference implementation of the same Stockham butterfly transforms an impulse into a flat
    ///   spectrum, the textbook pair. That is what makes the reference trustworthy (a plain <c>[Fact]</c>, so it runs
    ///   in the fast GPU-free lane too).</item>
    ///   <item>The GPU forward transform of a deterministic pseudo-random grid matches that reference elementwise.</item>
    ///   <item>Forward then inverse returns the original grid, which catches a sign or normalization error that a
    ///   self-consistent forward pass alone would not.</item>
    /// </list>
    ///
    /// Every stage is its own submit + drain, per the ordering contract on <see cref="IGpuCommandList"/>: each stage
    /// reads what the previous stage wrote, and chaining dependent dispatches inside one command list is not safe on
    /// every backend. N stays at 64 (12 dispatches per 2D transform) so the software rasterizers on CI are not asked
    /// to do real work.
    /// </summary>
    public sealed class ComputeFftGpuTests
    {
        const int N = 64;                     // transform size per axis; 64x64 complex grid
        const int Stages = 6;                 // log2(N)
        const uint GroupSize = 64;            // matches ComputeShaders.FftStage's local_size_x

        /// <summary>A complex value, laid out to match the GLSL <c>struct Complex { vec2 v; }</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct Cx
        {
            public float R, I;
            public Cx(float r, float i) { R = r; I = i; }
        }

        /// <summary>One stage's parameter block, matching the std140 uniform in <see cref="ComputeShaders.FftStage"/>
        /// (four uints then two floats; the block rounds up to 32 bytes).</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct FftParams
        {
            public uint N, Mh, Stride, LineStride;
            public float Sign, Scale;
            public float Pad0, Pad1;
        }

        // ---- CPU reference ----

        [Fact]
        public void CpuReferenceTurnsAnImpulseIntoAFlatSpectrum()
        {
            var grid = new Cx[N * N];
            grid[0] = new Cx(1f, 0f);   // impulse at the origin

            Cx[] spectrum = CpuFft2D(grid, inverse: false);

            for (int i = 0; i < spectrum.Length; i++)
            {
                Assert.True(Math.Abs(spectrum[i].R - 1f) < 1e-4f && Math.Abs(spectrum[i].I) < 1e-4f,
                    $"impulse -> flat spectrum: element {i} is ({spectrum[i].R}, {spectrum[i].I}), expected (1, 0)");
            }
        }

        // ---- GPU ----

        [GpuFact]
        public void GpuStockhamFftMatchesTheReferenceAndRoundTripsToIdentity()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            Cx[] input = PseudoRandomGrid();
            Cx[] cpuForward = CpuFft2D(input, inverse: false);

            using var fft = new GpuFft(dev);

            Cx[] gpuForward = fft.Run(input, inverse: false);
            float scale = MaxMagnitude(cpuForward);
            AssertClose(cpuForward, gpuForward, 1e-3f * scale, "GPU forward vs CPU reference");

            Cx[] roundTrip = fft.Run(gpuForward, inverse: true);
            AssertClose(input, roundTrip, 2e-3f, "forward then inverse vs the original grid");

            // The same textbook pair the CPU reference is checked against, now end to end on the device.
            var impulse = new Cx[N * N];
            impulse[0] = new Cx(1f, 0f);
            Cx[] flat = fft.Run(impulse, inverse: false);
            for (int i = 0; i < flat.Length; i++)
            {
                Assert.True(Math.Abs(flat[i].R - 1f) < 1e-3f && Math.Abs(flat[i].I) < 1e-3f,
                    $"GPU impulse -> flat spectrum: element {i} is ({flat[i].R}, {flat[i].I}), expected (1, 0)");
            }
        }

        /// <summary>Owns the two ping-pong storage buffers, the pipeline, and one uniform buffer + resource set per
        /// stage, so a run is just "upload, dispatch each stage, read back".</summary>
        sealed class GpuFft : IDisposable
        {
            readonly IGpuDevice _dev;
            readonly List<IDisposable> _owned = new();
            readonly IGpuComputePipeline _pipeline;
            readonly IGpuResourceLayout _layout;
            readonly IGpuBuffer _a, _b;

            public GpuFft(IGpuDevice dev)
            {
                _dev = dev;
                IGpuResourceFactory f = dev.Factory;

                var shader = f.CreateComputeShaderFromSpirv(ComputeShaders.FftStage);
                _owned.Add(shader);
                Assert.Equal(GroupSize, shader.ThreadGroupSizeX);

                _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute),
                    new GpuResourceLayoutElement("SrcBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
                    new GpuResourceLayoutElement("DstBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
                _owned.Add(_layout);

                _pipeline = f.CreateComputePipeline(new GpuComputePipelineDescription(shader, _layout));
                _owned.Add(_pipeline);

                uint stride = (uint)Marshal.SizeOf<Cx>();   // 8: the GLSL std430 stride of a Complex element
                uint bytes = (uint)(N * N) * stride;
                _a = f.CreateBuffer(new GpuBufferDescription(bytes, GpuBufferUsage.StructuredBufferReadWrite, stride));
                _b = f.CreateBuffer(new GpuBufferDescription(bytes, GpuBufferUsage.StructuredBufferReadWrite, stride));
                _owned.Add(_a);
                _owned.Add(_b);
            }

            /// <summary>Full 2D transform: a row sweep then a column sweep, log2(N) stages each.</summary>
            public Cx[] Run(Cx[] grid, bool inverse)
            {
                _dev.UpdateBuffer(_a, 0, grid);
                IGpuBuffer src = _a, dst = _b;

                // Rows: elements of one transform step by 1, transforms step by N. Columns: the reverse.
                foreach ((uint stride, uint lineStride) in new[] { (1u, (uint)N), ((uint)N, 1u) })
                {
                    for (int s = 0; s < Stages; s++)
                    {
                        var p = new FftParams
                        {
                            N = N,
                            Mh = 1u << s,
                            Stride = stride,
                            LineStride = lineStride,
                            Sign = inverse ? 1f : -1f,
                            // The 1/N normalization rides the last stage of each inverse axis sweep, so the two
                            // sweeps together contribute the 1/(N*N) an inverse 2D transform needs.
                            Scale = (inverse && s == Stages - 1) ? 1f / N : 1f,
                        };
                        DispatchStage(p, src, dst);
                        (src, dst) = (dst, src);
                    }
                }

                return GpuReadback.ReadBuffer<Cx>(_dev, src, N * N);
            }

            void DispatchStage(in FftParams p, IGpuBuffer src, IGpuBuffer dst)
            {
                IGpuResourceFactory f = _dev.Factory;
                using IGpuBuffer paramsBuf = f.CreateBuffer(new GpuBufferDescription(32, GpuBufferUsage.UniformBuffer));
                _dev.UpdateBuffer(paramsBuf, 0, p);
                using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(_layout, paramsBuf, src, dst));

                // One thread per butterfly: N/2 butterflies on each of N transforms.
                uint threads = (uint)(N * (N / 2));
                uint groups = (threads + GroupSize - 1) / GroupSize;

                using IGpuCommandList cl = f.CreateCommandList();
                cl.Begin();
                cl.SetComputePipeline(_pipeline);
                cl.SetComputeResourceSet(0, set);
                cl.Dispatch(groups, 1, 1);
                cl.End();
                _dev.Submit(cl);
                // Each stage reads the previous stage's output, so the submit boundary plus this drain is the
                // ordering. See the IGpuCommandList ordering contract.
                _dev.WaitForIdle();
            }

            public void Dispose()
            {
                _dev.WaitForIdle();
                for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            }
        }

        // ---- CPU reference implementation (same butterfly as ComputeShaders.FftStage) ----

        static Cx[] CpuFft2D(Cx[] grid, bool inverse)
        {
            var a = (Cx[])grid.Clone();
            var b = new Cx[grid.Length];
            float sign = inverse ? 1f : -1f;

            foreach ((int stride, int lineStride) in new[] { (1, N), (N, 1) })
            {
                for (int s = 0; s < Stages; s++)
                {
                    float scale = (inverse && s == Stages - 1) ? 1f / N : 1f;
                    Stage(a, b, 1 << s, stride, lineStride, sign, scale);
                    (a, b) = (b, a);
                }
            }
            return a;
        }

        static void Stage(Cx[] src, Cx[] dst, int mh, int stride, int lineStride, float sign, float scale)
        {
            int halfN = N / 2;
            for (int line = 0; line < N; line++)
            {
                int baseIndex = line * lineStride;
                for (int t = 0; t < halfN; t++)
                {
                    int k = t / mh;
                    int j = t - k * mh;

                    Cx a = src[baseIndex + t * stride];
                    Cx b = src[baseIndex + (t + halfN) * stride];

                    double ang = sign * 2.0 * Math.PI * j / (2 * mh);
                    var w = new Cx((float)Math.Cos(ang), (float)Math.Sin(ang));
                    var wb = new Cx(w.R * b.R - w.I * b.I, w.R * b.I + w.I * b.R);

                    int lo = k * 2 * mh + j;
                    dst[baseIndex + lo * stride] = new Cx((a.R + wb.R) * scale, (a.I + wb.I) * scale);
                    dst[baseIndex + (lo + mh) * stride] = new Cx((a.R - wb.R) * scale, (a.I - wb.I) * scale);
                }
            }
        }

        // ---- helpers ----

        // Deterministic so a failure is reproducible: a plain 32-bit LCG, not Random (whose sequence is not pinned).
        static Cx[] PseudoRandomGrid()
        {
            var grid = new Cx[N * N];
            uint state = 0x9E3779B9u;
            for (int i = 0; i < grid.Length; i++)
            {
                state = state * 1664525u + 1013904223u;
                float r = (state >> 8) / (float)(1 << 24) * 2f - 1f;
                state = state * 1664525u + 1013904223u;
                float im = (state >> 8) / (float)(1 << 24) * 2f - 1f;
                grid[i] = new Cx(r, im);
            }
            return grid;
        }

        static float MaxMagnitude(Cx[] values)
        {
            float max = 0f;
            foreach (Cx c in values) max = Math.Max(max, Math.Max(Math.Abs(c.R), Math.Abs(c.I)));
            return max;
        }

        static void AssertClose(Cx[] expected, Cx[] actual, float tolerance, string what)
        {
            Assert.Equal(expected.Length, actual.Length);
            float worst = 0f;
            int worstIndex = -1;
            for (int i = 0; i < expected.Length; i++)
            {
                float d = Math.Max(Math.Abs(expected[i].R - actual[i].R), Math.Abs(expected[i].I - actual[i].I));
                if (d > worst) { worst = d; worstIndex = i; }
            }
            Assert.True(worst <= tolerance,
                $"{what}: worst elementwise error {worst} > tolerance {tolerance} at element {worstIndex} " +
                $"(expected ({expected[worstIndex].R}, {expected[worstIndex].I}), " +
                $"got ({actual[worstIndex].R}, {actual[worstIndex].I}))");
        }
    }
}
