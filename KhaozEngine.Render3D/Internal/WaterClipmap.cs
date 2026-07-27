using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>One vertex of the <see cref="WaterGridMode.Clipmap"/> surface grid. 24 bytes, matching the vertex
    /// layout <c>WaterRenderer</c> builds for the clipmap pipeline and the three inputs
    /// <c>ShaderSources.WaterClipmapVert</c> declares.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaterClipmapVertex
    {
        /// <summary>Still-water world position. Snapped to this vertex's own ring lattice, then clamped into the
        /// plane rectangle (a fixed world rectangle, so clamping does not un-lock it from the world).</summary>
        public Vector3 Position;

        /// <summary>Half the world-space vector between this vertex's two COARSE neighbours, for a vertex on a
        /// ring's outer boundary that has no counterpart on the next ring out's lattice. The shader evaluates the
        /// surface at <c>Position +/- Stitch</c> and averages, which lands the vertex exactly on the coarse ring's
        /// edge segment and closes the T-junction with no skirt and no degenerate triangle. Zero everywhere else,
        /// which is the single-tap path.</summary>
        public Vector2 Stitch;

        /// <summary>The world-space sample spacing this vertex band-limits to: its own ring's cell size, or the
        /// NEXT ring out's cell size when it sits on the shared boundary (so both sides of that boundary evaluate
        /// the identical low-pass and meet exactly). Feeds the per-cascade mip selection in
        /// <see cref="WaterClipmap.MipLevel"/>.</summary>
        public float Cell;
    }

    /// <summary>
    /// Pure, GPU-free layout math for the world-locked clipmap water grid (<see cref="WaterGridMode.Clipmap"/>):
    /// ring sizing, per-ring world snapping, the vertex/index build, and the per-ring mip selection the shaders
    /// mirror. No GPU state and no allocations, so all of it is headless-testable - the same split
    /// <see cref="WaterMath"/> keeps for the camera-focused grid.
    /// <para>
    /// <b>The structure.</b> <c>levels</c> concentric square levels. Level 0 is a solid <c>ringCells</c>-squared
    /// block of cells of size <c>cell</c>; level L is a RING of <c>ringCells</c>-squared cells of size
    /// <c>cell * 2^L</c> with a <c>ringCells/2</c>-squared hole in it, and that hole is exactly level L-1's extent.
    /// Every level therefore doubles its cell size and its coverage, and the whole thing is one vertex buffer and
    /// one index buffer.
    /// </para>
    /// <para>
    /// <b>The snap, which is the entire point.</b> Level L's origin is the camera XZ rounded to the nearest
    /// multiple of <c>2 * cellSize(L)</c>. Rounding to TWICE the cell (rather than to the cell) is what makes the
    /// nesting exact: level L-1's origin is then a multiple of <c>cellSize(L)</c>, so level L-1's outer boundary
    /// lands on level L's lattice, and the hole can be an exact whole number of level-L cells offset by
    /// <c>d in {-1, 0, +1}</c> cells from level L's own centre. Nothing here interpolates and nothing slides: as
    /// the camera moves, a level either does not move at all or jumps by two of its own cells, and a two-cell jump
    /// maps its lattice onto itself, so every vertex that is still in range is at the same world position it was
    /// at before. That is what stops the surface being resampled every frame.
    /// </para>
    /// <para>
    /// <b>Why not centre-snap the existing warped grid instead.</b> Under
    /// <see cref="WaterSettings.GridFocusBias"/> greater than 1 the grid's offsets are non-uniform and share no
    /// common quantum, so there is no snap that leaves the vertex set invariant. Measured and recorded on
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/296">#296</see>.
    /// </para>
    /// </summary>
    internal static class WaterClipmap
    {
        /// <summary>Hard cap on ring levels. Ten levels doubles the cell size 512-fold, which covers a
        /// kilometres-wide plane from any half-metre base cell; the cap exists so a nonsense setting cannot ask for
        /// an unbounded buffer.</summary>
        public const int MaxLevels = 10;

        /// <summary>Smallest ring size, in cells per side. Below this the hole (half the ring) plus the +/-1 cell
        /// of snap slack leaves no ring at all.</summary>
        public const int MinRingCells = 8;

        /// <summary>Largest ring size, in cells per side. 256 cells per side is 66049 vertices per level before
        /// the hole, well past any sane budget; the cap is a guard, not a recommendation.</summary>
        public const int MaxRingCells = 256;

        /// <summary>Round a requested ring size to a legal one: clamped to
        /// <see cref="MinRingCells"/>..<see cref="MaxRingCells"/> and rounded DOWN to a multiple of 4. The multiple
        /// of 4 is load-bearing rather than tidy - the hole is <c>ringCells/2</c> cells across and its half-width
        /// in the FINER level's cells is <c>ringCells/4</c>, so both have to be whole numbers.</summary>
        public static int ClampRingCells(int requested)
        {
            int n = Math.Clamp(requested, MinRingCells, MaxRingCells);
            return n - (n & 3);
        }

        /// <summary>Cell size of level <paramref name="level"/>: the base cell doubled once per level out.</summary>
        public static float CellSize(float baseCell, int level) => baseCell * (1 << level);

        /// <summary>
        /// Which multiple of TWICE <paramref name="cellSize"/> a world coordinate snaps to. The integer form is
        /// the one the renderer caches on: the built geometry is a pure function of these indices plus the plane
        /// and the ring settings, so an unchanged set means an unchanged grid and no rebuild and no upload at all.
        /// See the class note for why the quantum is twice the cell and not once.
        /// </summary>
        public static long SnapIndex(float coordinate, float cellSize)
            => (long)MathF.Round(coordinate / (2f * cellSize), MidpointRounding.AwayFromZero);

        /// <summary>The world origin <see cref="SnapIndex"/> names.</summary>
        public static float SnapOrigin(float coordinate, float cellSize)
            => SnapIndex(coordinate, cellSize) * (2f * cellSize);

        /// <summary>Every level's snap index for one axis pair, into <paramref name="outX"/>/<paramref name="outZ"/>
        /// (each at least <paramref name="levels"/> long). Feed it the CLAMPED focus (see
        /// <see cref="ClampFocus"/>), the same one <see cref="Build"/> gets, or the cache key describes a different
        /// grid from the one that would be built.</summary>
        public static void SnapIndices(float focusX, float focusZ, float baseCell, int levels,
            Span<long> outX, Span<long> outZ)
        {
            float cell = MathF.Max(baseCell, 1e-4f);
            for (int l = 0; l < levels; l++)
            {
                float c = CellSize(cell, l);
                outX[l] = SnapIndex(focusX, c);
                outZ[l] = SnapIndex(focusZ, c);
            }
        }

        /// <summary>The focus point clamped into the plane's rectangle, so a camera off the edge of the water still
        /// centres the clipmap on the nearest water rather than on empty space. Mirrors the clamp
        /// <see cref="WaterMath.BuildGridPositions"/> applies to its focus.</summary>
        public static Vector2 ClampFocus(in WaterPlane plane, float focusX, float focusZ) => new(
            Math.Clamp(focusX, plane.CenterX - plane.HalfExtentX, plane.CenterX + plane.HalfExtentX),
            Math.Clamp(focusZ, plane.CenterZ - plane.HalfExtentZ, plane.CenterZ + plane.HalfExtentZ));

        /// <summary>
        /// How many levels it takes for the outermost ring to cover <paramref name="plane"/> from ANY camera
        /// position inside it - which is why the target is twice the plane's larger half-extent rather than one
        /// times it: the clipmap centres on the camera, and a camera in one corner has to reach the opposite one.
        /// Clamped to 1..<see cref="MaxLevels"/>. This is what <see cref="WaterSettings.ClipmapLevels"/> left at 0
        /// resolves to.
        /// </summary>
        public static int LevelsFor(in WaterPlane plane, float baseCell, int ringCells)
        {
            float need = 2f * MathF.Max(plane.HalfExtentX, plane.HalfExtentZ);
            float level0Half = ringCells * 0.5f * MathF.Max(baseCell, 1e-4f);
            if (!(need > level0Half)) return 1;
            int levels = 1 + (int)MathF.Ceiling(MathF.Log2(need / level0Half));
            return Math.Clamp(levels, 1, MaxLevels);
        }

        /// <summary>Vertices the build writes: a full <c>(ringCells + 1)</c>-squared block per level. The hole's
        /// interior vertices are written but never indexed - keeping the per-level vertex BLOCK a fixed size and a
        /// fixed layout means only the index set has to care where the hole landed, and it costs 12 per cent of an
        /// upload that most frames does not happen at all.</summary>
        public static int VertexCount(int levels, int ringCells) => levels * (ringCells + 1) * (ringCells + 1);

        /// <summary>Indices the build writes. Constant for a given (levels, ringCells): the hole is always
        /// <c>ringCells/2</c> cells square and always lands wholly inside the ring, so its POSITION varies with the
        /// snap but the quad COUNT never does.</summary>
        public static int IndexCount(int levels, int ringCells)
        {
            int quads = ringCells * ringCells;
            int ring = quads - (ringCells / 2) * (ringCells / 2);
            return (quads + (levels - 1) * ring) * 6;
        }

        /// <summary>
        /// Mip level to sample a cascade at so its content is low-passed to <paramref name="sampleSpacing"/>'s
        /// Nyquist. <paramref name="texelMetres"/> is the cascade's world-space texel size (its tile over its
        /// resolution), so mip <c>m</c> carries texels of <c>texelMetres * 2^m</c> and a shortest wavelength of
        /// twice that. Solving <c>2 * texelMetres * 2^m &gt;= samplesPerWavelength * sampleSpacing</c> gives the
        /// log2 below.
        /// <para>
        /// <paramref name="samplesPerWavelength"/> 2 is plain Nyquist (the mip's texel size matches the sample
        /// spacing) and is the shipped default; higher oversamples and is softer. <paramref name="maxMip"/> 0
        /// returns 0 for everything, which is the pre-mip behaviour exactly, and so does a
        /// <paramref name="samplesPerWavelength"/> of 0, which is
        /// <see cref="WaterSettings.FootprintSamples"/>' documented band-limit-off switch on the fragment side.
        /// </para>
        /// </summary>
        public static float MipLevel(float sampleSpacing, float texelMetres, float samplesPerWavelength, float maxMip)
        {
            if (maxMip <= 0f || texelMetres <= 1e-9f || sampleSpacing <= 0f || samplesPerWavelength <= 0f) return 0f;
            float want = sampleSpacing * MathF.Max(samplesPerWavelength, 1f) / (2f * texelMetres);
            if (want <= 1f) return 0f;
            return MathF.Min(MathF.Log2(want), maxMip);
        }

        /// <summary>Mip levels a square cascade map of <paramref name="resolution"/> texels per side carries when
        /// it is given a full chain: <c>floor(log2(n)) + 1</c>, i.e. down to the 1x1 level.</summary>
        public static int MipCount(int resolution)
        {
            int mips = 1;
            for (int n = Math.Max(resolution, 1); n > 1; n >>= 1) mips++;
            return mips;
        }

        /// <summary>
        /// Build the whole clipmap for one plane into <paramref name="vertices"/> (at least
        /// <see cref="VertexCount"/> long) and <paramref name="indices"/> (at least <see cref="IndexCount"/>),
        /// returning the vertex count and reporting the index count.
        /// </summary>
        /// <param name="plane">The queued plane. Its rectangle clamps every vertex, so the surface never spills
        /// past the water body even though the clipmap is centred on the camera.</param>
        /// <param name="focusX">World X to centre on, ALREADY clamped into the plane (see
        /// <see cref="ClampFocus"/>).</param>
        /// <param name="focusZ">World Z to centre on, already clamped.</param>
        /// <param name="baseCell">Level 0's cell size, world units.</param>
        /// <param name="ringCells">Cells per side per level; must already be <see cref="ClampRingCells"/>ed.</param>
        /// <param name="levels">Level count, 1..<see cref="MaxLevels"/>.</param>
        /// <param name="vertices">Receives the vertex block.</param>
        /// <param name="indices">Receives the triangle-list indices.</param>
        /// <param name="indexCount">Receives the index count actually written.</param>
        public static int Build(in WaterPlane plane, float focusX, float focusZ, float baseCell, int ringCells,
            int levels, Span<WaterClipmapVertex> vertices, Span<uint> indices, out int indexCount)
        {
            int n = ringCells;
            int stride = n + 1;
            int perLevel = stride * stride;
            float cell = MathF.Max(baseCell, 1e-4f);
            levels = Math.Clamp(levels, 1, MaxLevels);

            float minX = plane.CenterX - plane.HalfExtentX, maxX = plane.CenterX + plane.HalfExtentX;
            float minZ = plane.CenterZ - plane.HalfExtentZ, maxZ = plane.CenterZ + plane.HalfExtentZ;

            Span<float> originX = stackalloc float[MaxLevels];
            Span<float> originZ = stackalloc float[MaxLevels];
            for (int l = 0; l < levels; l++)
            {
                float c = CellSize(cell, l);
                originX[l] = SnapOrigin(focusX, c);
                originZ[l] = SnapOrigin(focusZ, c);
            }

            int written = 0;
            int ic = 0;
            for (int l = 0; l < levels; l++)
            {
                float c = CellSize(cell, l);
                // The cell size the SHARED boundary with the next ring out is band-limited to. The outermost level
                // has no neighbour, so it keeps its own and never stitches.
                bool hasCoarser = l + 1 < levels;
                float coarse = hasCoarser ? CellSize(cell, l + 1) : c;
                float ox = originX[l], oz = originZ[l];
                int half = n / 2;

                for (int j = 0; j <= n; j++)
                {
                    bool edgeZ = j == 0 || j == n;
                    for (int i = 0; i <= n; i++)
                    {
                        bool edgeX = i == 0 || i == n;
                        bool onBoundary = edgeX || edgeZ;

                        float wx = Math.Clamp(ox + (i - half) * c, minX, maxX);
                        float wz = Math.Clamp(oz + (j - half) * c, minZ, maxZ);
                        Vector2 stitch = Vector2.Zero;

                        if (onBoundary && hasCoarser)
                        {
                            // A boundary vertex sits on the coarse lattice exactly when its index is even (the
                            // half-offset is even because ringCells is a multiple of 4), and the corners are
                            // always even, so the two axes can never both want a stitch.
                            if (edgeZ && (i & 1) != 0)
                            {
                                float lo = Math.Clamp(ox + (i - 1 - half) * c, minX, maxX);
                                float hi = Math.Clamp(ox + (i + 1 - half) * c, minX, maxX);
                                wx = (lo + hi) * 0.5f;
                                stitch = new Vector2((hi - lo) * 0.5f, 0f);
                            }
                            else if (edgeX && (j & 1) != 0)
                            {
                                float lo = Math.Clamp(oz + (j - 1 - half) * c, minZ, maxZ);
                                float hi = Math.Clamp(oz + (j + 1 - half) * c, minZ, maxZ);
                                wz = (lo + hi) * 0.5f;
                                stitch = new Vector2(0f, (hi - lo) * 0.5f);
                            }
                        }

                        vertices[written++] = new WaterClipmapVertex
                        {
                            Position = new Vector3(wx, plane.SurfaceY, wz),
                            Stitch = stitch,
                            Cell = onBoundary && hasCoarser ? coarse : c,
                        };
                    }
                }

                // The hole: exactly level l-1's extent, expressed in level l's cells. Both origins are multiples
                // of c (level l's of 2c, level l-1's of c), so the offset is a whole number of cells and lands in
                // {-1, 0, +1}.
                int holeX0 = int.MinValue, holeZ0 = int.MinValue, holeSpan = n / 2;
                if (l > 0)
                {
                    int dx = (int)MathF.Round((originX[l - 1] - ox) / c, MidpointRounding.AwayFromZero);
                    int dz = (int)MathF.Round((originZ[l - 1] - oz) / c, MidpointRounding.AwayFromZero);
                    holeX0 = half + dx - n / 4;
                    holeZ0 = half + dz - n / 4;
                }

                int baseVertex = l * perLevel;
                for (int z = 0; z < n; z++)
                {
                    bool holeRow = l > 0 && z >= holeZ0 && z < holeZ0 + holeSpan;
                    for (int x = 0; x < n; x++)
                    {
                        if (holeRow && x >= holeX0 && x < holeX0 + holeSpan) continue;
                        uint i0 = (uint)(baseVertex + z * stride + x);
                        uint i1 = i0 + 1;
                        uint i2 = (uint)(baseVertex + (z + 1) * stride + x);
                        uint i3 = i2 + 1;
                        // Clockwise winding, matching WaterMath.BuildGridIndices and the engine's
                        // GpuFrontFace.Clockwise convention.
                        indices[ic++] = i0; indices[ic++] = i2; indices[ic++] = i1;
                        indices[ic++] = i1; indices[ic++] = i2; indices[ic++] = i3;
                    }
                }
            }

            indexCount = ic;
            return written;
        }
    }
}
