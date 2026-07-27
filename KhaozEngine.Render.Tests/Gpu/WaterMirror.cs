using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The CPU mirror the water-grid acceptance metric measures THROUGH: an FFT ocean's cascades read back off the
    /// GPU (<see cref="Ocean"/>) and either surface grid built and displaced over them (<see cref="Surface"/>),
    /// with the point query the metric needs.
    /// <para>
    /// Its own type and its own file because it is a HARNESS, not a test. It mirrors the vertex stage's sampling
    /// and both grids' layout, which is a body of maths with its own reasons, and every deviation from
    /// <c>ShaderSources.WaterVert</c> / <c>WaterClipmapVert</c> in here is a wrong MEASUREMENT rather than a wrong
    /// render - so it fails silently and deserves to be read on its own. Keeping it inside the test class also
    /// buried the handful of assertions the whole thing exists to serve.
    /// </para>
    /// </summary>
    internal static class WaterMirror
    {
        /// <summary>FFT resolution the acceptance measurement runs at. Small deliberately: the software CI legs
        /// are slow (KhaozEngine#332), and what is under test is the grid, not the transform.</summary>
        public const int N = 64;

        /// <summary>Cascade count the measurement runs at.</summary>
        public const int Cascades = 3;

        /// <summary>A built grid, displaced, with the point query the metric needs.</summary>
        public sealed class Surface
        {
            // Camera-focused: monotone warped axes plus a displaced height per node.
            float[] _xs = Array.Empty<float>(), _zs = Array.Empty<float>();
            float[] _h = Array.Empty<float>();
            // Clipmap: per-level origins, cell sizes and a displaced height per node.
            WaterClipmapVertex[] _verts = Array.Empty<WaterClipmapVertex>();
            float[] _clipH = Array.Empty<float>();
            float _cell;
            Vector3 _origin;
            int _ring, _levels;
            float[] _ox = Array.Empty<float>(), _oz = Array.Empty<float>();
            bool _isClip;

            public static Surface Focused(in WaterPlane plane, in Ocean maps, WaterSettings settings, float camX)
            {
                const int n = WaterMath.GridResolution;
                var pos = new Vector3[n * n];
                var scratch = new float[2 * n];
                WaterMath.BuildGridPositions(plane, camX, 0f, settings.GridFocusBias, pos, scratch);
                var s = new Surface { _xs = new float[n], _zs = new float[n], _h = new float[n * n] };
                for (int i = 0; i < n; i++) { s._xs[i] = pos[i].X; s._zs[i] = pos[i * n].Z; }
                for (int i = 0; i < n * n; i++)
                    // No mip chain is bound on this path, so every vertex samples LOD 0 - which IS the defect.
                    s._h[i] = pos[i].Y + maps.Displace(pos[i].X, pos[i].Z, 0f, settings).Y;
                return s;
            }

            public static Surface Clip(in WaterPlane plane, in Ocean maps, WaterSettings settings, float camX,
                Vector3 renderOrigin = default)
            {
                float cell = settings.ClipmapCellSize;
                int ring = WaterClipmap.ClampRingCells(settings.ClipmapRingCells);
                int levels = WaterClipmap.LevelsFor(plane, cell, ring);
                var verts = new WaterClipmapVertex[WaterClipmap.VertexCount(levels, ring)];
                var indices = new uint[WaterClipmap.IndexCount(levels, ring)];
                Vector2 focus = WaterClipmap.ClampFocus(plane, camX, 0f);
                int vc = WaterClipmap.Build(plane, focus.X, focus.Y, cell, ring, levels,
                    settings.ClipmapGeomorphBand, verts, indices, out _, renderOrigin);

                var s = new Surface
                {
                    _isClip = true, _verts = verts, _clipH = new float[vc], _cell = cell,
                    _ring = ring, _levels = levels, _ox = new float[levels], _oz = new float[levels],
                    _origin = renderOrigin,
                };
                // Ring origins reduced exactly as Build reduces them, so the cell lookup below lands in the same
                // frame the vertices were written in.
                for (int l = 0; l < levels; l++)
                {
                    float c = WaterClipmap.CellSize(cell, l);
                    s._ox[l] = WaterClipmap.SnapOrigin(focus.X, c) - renderOrigin.X;
                    s._oz[l] = WaterClipmap.SnapOrigin(focus.Y, c) - renderOrigin.Z;
                }
                for (int i = 0; i < vc; i++)
                {
                    WaterClipmapVertex v = verts[i];
                    // Mirrors the vertex stage's tap loop exactly, geomorph included: weights
                    // (1 - Morph, Morph/2, Morph/2) over the vertex's own position and its two coarse neighbours,
                    // collapsing to the single tap when the vertex is already on the coarse lattice, and every tap
                    // band-limited to this vertex's own (already morphed) Cell. Zero-weight taps are SKIPPED, not
                    // evaluated at 0, which is what keeps an un-morphed vertex a single evaluation.
                    Vector3 w = v.Coarse == Vector2.Zero
                        ? new Vector3(1f, 0f, 0f)
                        : new Vector3(1f - v.Morph, 0.5f * v.Morph, 0.5f * v.Morph);
                    float sum = 0f;
                    for (int t = 0; t < 3; t++)
                    {
                        float tw = t == 0 ? w.X : (t == 1 ? w.Y : w.Z);
                        if (tw <= 0f) continue;
                        Vector2 o = t == 0 ? Vector2.Zero : (t == 1 ? -v.Coarse : v.Coarse);
                        // The shader's aXz: the cascade maps are indexed by ABSOLUTE world position, so the
                        // origin goes back on here and nowhere else.
                        float sx = v.Position.X + o.X + renderOrigin.X, sz = v.Position.Z + o.Y + renderOrigin.Z;
                        sum += (v.Position.Y + renderOrigin.Y + maps.Displace(sx, sz, v.Cell, settings).Y) * tw;
                    }
                    s._clipH[i] = sum;
                }
                return s;
            }

            /// <summary>Height of the drawn surface at an ABSOLUTE world XZ. The clipmap's own geometry is in the
            /// render frame, so the query converts into it and the result converts back - which is exactly the
            /// round trip the shipping path makes, and therefore what a re-derivation of the metric under a render
            /// origin has to exercise.</summary>
            public float HeightAt(float x, float z)
                => _isClip ? ClipHeight(x - _origin.X, z - _origin.Z) + _origin.Y : FocusedHeight(x, z);

            float FocusedHeight(float x, float z)
            {
                const int n = WaterMath.GridResolution;
                int i = Cell(_xs, x), j = Cell(_zs, z);
                float u = (x - _xs[i]) / (_xs[i + 1] - _xs[i]);
                float v = (z - _zs[j]) / (_zs[j + 1] - _zs[j]);
                return Bary(u, v, _h[j * n + i], _h[j * n + i + 1], _h[(j + 1) * n + i], _h[(j + 1) * n + i + 1]);
            }

            float ClipHeight(float x, float z)
            {
                int stride = _ring + 1, perLevel = stride * stride;
                for (int l = 0; l < _levels; l++)
                {
                    float c = WaterClipmap.CellSize(_cell, l);
                    float half = _ring * 0.5f * c;
                    float lx = (x - (_ox[l] - half)) / c, lz = (z - (_oz[l] - half)) / c;
                    if (lx < 0f || lz < 0f || lx >= _ring || lz >= _ring) continue;
                    int i = (int)lx, j = (int)lz;
                    int b = l * perLevel + j * stride + i;
                    return Bary(lx - i, lz - j, _clipH[b], _clipH[b + 1], _clipH[b + stride], _clipH[b + stride + 1]);
                }
                return 0f;   // outside the outermost ring: no surface, and no probe reaches here
            }

            /// <summary>Interpolate over the quad's TWO triangles, matching the (i0, i2, i1) / (i1, i2, i3)
            /// triangulation the index builders emit, so the metric reads the surface that is actually drawn
            /// rather than a bilinear approximation of it.</summary>
            static float Bary(float u, float v, float h00, float h10, float h01, float h11)
                => u + v <= 1f
                    ? h00 + (h10 - h00) * u + (h01 - h00) * v
                    : h11 + (h01 - h11) * (1f - u) + (h10 - h11) * (1f - v);

            static int Cell(float[] axis, float value)
            {
                int lo = 0, hi = axis.Length - 2;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (axis[mid] <= value) lo = mid; else hi = mid - 1;
                }
                return Math.Clamp(lo, 0, axis.Length - 2);
            }
        }

        // ---- The maps ----------------------------------------------------------------------------------------

        /// <summary>One frame's displacement cascades, read back and pyramided, plus the sampling the vertex stage
        /// does over them.</summary>
        public readonly struct Ocean
        {
            /// <summary>[cascade][mip] as tightly packed rgba, 4 floats per texel.</summary>
            public float[][][] Mips { get; init; }
            public float[] Tiles { get; init; }
            public float MaxMip { get; init; }

            /// <summary>Mirrors the vertex stage's cascade sum exactly, at the identity sampling frame: per
            /// cascade, a half-texel-offset wrapping trilinear tap at the level <see cref="WaterClipmap.MipLevel"/>
            /// picks for <paramref name="spacing"/>. <paramref name="spacing"/> 0 is the camera-focused path, where
            /// there is no chain and the level is 0.</summary>
            public Vector3 Displace(float x, float z, float spacing, WaterSettings settings)
            {
                var sum = Vector3.Zero;
                for (int c = 0; c < Tiles.Length; c++)
                {
                    float texel = Tiles[c] / N;
                    float lod = WaterClipmap.MipLevel(texel <= 0f ? 0f : spacing, texel,
                        settings.ClipmapBandLimitSamples, MaxMip);
                    int m0 = (int)MathF.Floor(lod), m1 = Math.Min(m0 + 1, Mips[c].Length - 1);
                    Vector3 a = Tap(Mips[c][m0], N >> m0, x, z, Tiles[c]);
                    if (m1 == m0) { sum += a; continue; }
                    sum += Vector3.Lerp(a, Tap(Mips[c][m1], N >> m1, x, z, Tiles[c]), lod - m0);
                }
                return sum;
            }

            /// <summary>Wrapping bilinear tap, in the shader's own coordinates: normalized uv is
            /// <c>xz / tile + 0.5 / resolution</c> at every level, and the hardware scales that by the LEVEL's
            /// size.</summary>
            static Vector3 Tap(float[] level, int size, float x, float z, float tile)
            {
                float u = (x / tile + 0.5f / N) * size - 0.5f;
                float v = (z / tile + 0.5f / N) * size - 0.5f;
                int x0 = (int)MathF.Floor(u), z0 = (int)MathF.Floor(v);
                float fx = u - x0, fz = v - z0;
                Vector3 a = Texel(level, size, x0, z0), b = Texel(level, size, x0 + 1, z0);
                Vector3 c = Texel(level, size, x0, z0 + 1), d = Texel(level, size, x0 + 1, z0 + 1);
                return Vector3.Lerp(Vector3.Lerp(a, b, fx), Vector3.Lerp(c, d, fx), fz);
            }

            static Vector3 Texel(float[] level, int size, int x, int z)
            {
                int xi = ((x % size) + size) % size, zi = ((z % size) + size) % size;
                int o = (zi * size + xi) * 4;
                return new Vector3(level[o], level[o + 1], level[o + 2]);
            }
        }

        public static Ocean Capture(IGpuDevice dev, OceanFftProducer producer, WaterSettings settings, float time)
        {
            using (IGpuCommandList cl = dev.Factory.CreateCommandList())
            {
                cl.Begin();
                Assert.True(producer.Update(cl, settings, time, wantMips: true),
                    "the producer refused to run on a compute device");
                cl.End();
                dev.Submit(cl);
                dev.WaitForIdle();
            }

            var mips = new float[Cascades][][];
            var tiles = new float[Cascades];
            int levels = WaterClipmap.MipCount(N);
            for (int c = 0; c < Cascades; c++)
            {
                tiles[c] = producer.TileMetres[c];
                mips[c] = new float[levels][];
                mips[c][0] = ReadLevel(dev, producer.Map, 0, (uint)c, N);
                // The chain itself is box-downsampled here rather than read back level by level: the GPU's chain is
                // separately asserted to BE that box filter (AssertTheGpuChainIsABoxFilter), which is the cheaper
                // way round and pins the semantics as well as the values.
                for (int m = 1; m < levels; m++) mips[c][m] = Downsample(mips[c][m - 1], N >> (m - 1));
            }
            return new Ocean { Mips = mips, Tiles = tiles, MaxMip = producer.MaxMip };
        }

        /// <summary>
        /// The one thing about the mip chain that cannot be reasoned about from the shader side: that
        /// <c>GenerateMipmaps</c> ran AFTER the compute pass wrote the base level and produced the box filter the
        /// band limit assumes. Both halves are backend-specific (the copy is what forces the synchronisation, and
        /// each backend forces it differently), so this is checked on every backend rather than argued.
        /// <para>
        /// This half reads after a drain, so it pins the FILTER. The ORDERING hazard the shipping path actually
        /// runs into is a consumer of the chain sitting in the same still-open command list, which a drain hides -
        /// <c>WaterClipmapAcceptanceTests.TheMipChainIsFreshToALaterCommandInTheSameList</c> covers that, and the end-to-end render test
        /// exercises it through a real draw.
        /// </para>
        /// </summary>
        public static void AssertTheGpuChainIsABoxFilter(IGpuDevice dev, OceanFftProducer producer, in Ocean maps)
        {
            Assert.Equal(WaterClipmap.MipCount(N) - 1, (int)maps.MaxMip);
            for (uint layer = 0; layer < 2 * Cascades; layer++)
            {
                float[] baseLevel = ReadLevel(dev, producer.Map, 0, layer, N);
                float[] gpu = ReadLevel(dev, producer.Map, 1, layer, N / 2);
                float[] cpu = Downsample(baseLevel, N);


                float scale = 0f;
                foreach (float v in cpu) scale = MathF.Max(scale, MathF.Abs(v));
                float tolerance = MathF.Max(5e-3f * scale, 1e-5f);
                float worst = 0f;
                for (int i = 0; i < cpu.Length; i++) worst = MathF.Max(worst, MathF.Abs(cpu[i] - gpu[i]));
                Assert.True(worst <= tolerance,
                    $"layer {layer} mip 1 is off the box downsample of mip 0 by {worst} (tolerance {tolerance}). " +
                    "Either GenerateMipmaps did not see the compute pass's writes, or the chain is not a box " +
                    "filter and the per-ring band limit is selecting levels that do not mean what it thinks.");
            }
        }

        public static float[] Downsample(float[] level, int size)
        {
            int half = size / 2;
            var outp = new float[half * half * 4];
            for (int z = 0; z < half; z++)
            {
                for (int x = 0; x < half; x++)
                {
                    for (int ch = 0; ch < 4; ch++)
                    {
                        float a = level[((2 * z) * size + 2 * x) * 4 + ch];
                        float b = level[((2 * z) * size + 2 * x + 1) * 4 + ch];
                        float c = level[((2 * z + 1) * size + 2 * x) * 4 + ch];
                        float d = level[((2 * z + 1) * size + 2 * x + 1) * 4 + ch];
                        outp[(z * half + x) * 4 + ch] = (a + b + c + d) * 0.25f;
                    }
                }
            }
            return outp;
        }

        /// <summary>Read one mip level of one array layer of an rgba16f texture back as floats, 4 per texel. The
        /// half-float format has no <c>GpuReadback</c> helper, so this is the same hand-rolled staging copy
        /// <c>OceanFftGpuTests</c> uses, with the mip level opened up.</summary>
        public static float[] ReadLevel(IGpuDevice dev, IGpuTexture src, uint mip, uint layer, int size)
        {
            IGpuResourceFactory f = dev.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)size, (uint)size, GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.CopyTextureSubresource(src, mip, layer, staging, (uint)size, (uint)size);
                cl.End();
                dev.Submit(cl);
                dev.WaitForIdle();
            }
            return MapStaging(dev, staging, size);
        }

        /// <summary>Map a square rgba16f staging texture out as floats, 4 per texel, row-major.</summary>
        public static float[] MapStaging(IGpuDevice dev, IGpuTexture staging, int size)
        {
            var result = new float[size * size * 4];
            var row = new byte[size * 4 * 2];
            MappedData map = dev.Map(staging, GpuMapMode.Read);
            try
            {
                for (int y = 0; y < size; y++)
                {
                    Marshal.Copy(IntPtr.Add(map.Data, (int)(y * map.RowPitch)), row, 0, row.Length);
                    for (int i = 0; i < size * 4; i++) result[y * size * 4 + i] = (float)BitConverter.ToHalf(row, i * 2);
                }
            }
            finally
            {
                dev.Unmap(staging);
            }
            return result;
        }
    }
}
