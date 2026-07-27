using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>One vertex of the <see cref="WaterGridMode.Clipmap"/> surface grid. 28 bytes, matching the vertex
    /// layout <c>WaterRenderer</c> builds for the clipmap pipeline and the four inputs
    /// <c>ShaderSources.WaterClipmapVert</c> declares.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaterClipmapVertex
    {
        /// <summary>Still-water world position. Snapped to this vertex's own ring lattice, then clamped into the
        /// plane rectangle (a fixed world rectangle, so clamping does not un-lock it from the world).</summary>
        public Vector3 Position;

        /// <summary>
        /// Half the world-space vector between the two nodes of the NEXT RING OUT's lattice that this vertex sits
        /// between. Zero for a vertex that is itself on the coarse lattice (both ring indices even) and for the
        /// outermost ring, which has no coarser neighbour.
        /// <para>
        /// The shader evaluates the surface at <c>Position +/- Coarse</c> and mixes those two toward its own
        /// single-tap evaluation by <see cref="Morph"/>, so at <c>Morph = 1</c> the vertex lands exactly on the
        /// segment the coarse ring draws between those two nodes. At the ring's outer boundary
        /// <see cref="Morph"/> is always 1, which is precisely the stitch that closes the T-junction with no skirt
        /// and no degenerate triangle - the geomorph generalizes it inward rather than sitting beside it.
        /// </para>
        /// <para>
        /// The three off-lattice cases are all two-tap, including the diagonal one. A vertex with BOTH indices odd
        /// sits at the centre of a coarse quad, and the coarse surface there is not the average of four corners
        /// but the average of the two the coarse triangulation's diagonal runs between - the index builders emit
        /// <c>(i0, i2, i1) / (i1, i2, i3)</c>, so that diagonal is <c>i1</c> to <c>i2</c> and the offset is the
        /// anti-diagonal <c>(+c, -c)</c>.
        /// </para>
        /// </summary>
        public Vector2 Coarse;

        /// <summary>The world-space sample spacing this vertex band-limits to, ALREADY morphed: its own ring's cell
        /// size blended toward the next ring out's by <see cref="Morph"/>. Feeds the per-cascade mip selection in
        /// <see cref="WaterClipmap.MipLevel"/>. Precomputed here rather than blended in the shader because the
        /// weight is static per grid build, so a frame where no ring snapped does no work for it at all.</summary>
        public float Cell;

        /// <summary>How far this vertex has morphed toward the next ring out's evaluation, 0..1. 0 is its own
        /// ring, pure; 1 is exactly what the coarse ring would draw here (same position, same band limit). Ramps
        /// over the outer <see cref="WaterSettings.ClipmapGeomorphBand"/> of the ring and is 1 on the outer
        /// boundary, which is what turns the LOD change from a step into a fade.</summary>
        public float Morph;
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

        /// <summary>
        /// How far the vertex at ring indices (<paramref name="i"/>, <paramref name="j"/>) has morphed toward the
        /// next ring out, 0..1. Pure, static per grid build, and the whole of the geomorph's tuning.
        /// <para>
        /// The ramp is radial in CHEBYSHEV distance from the ring's centre (<c>max(|di|, |dj|)</c>), because that
        /// is the metric the square rings are actually laid out in: it is constant along a ring's own perimeter,
        /// so every vertex on the boundary reaches exactly 1 and the two sides of the boundary meet whatever the
        /// corner does. A Euclidean radius would reach 1 at the edge midpoints and overshoot at the corners.
        /// </para>
        /// <para>
        /// <paramref name="band"/> 0 gives back the pre-geomorph grid EXACTLY: 1 on the boundary (the stitch) and
        /// 0 everywhere else, by an early return rather than by a ramp that happens to be degenerate. That is the
        /// switch that keeps <see cref="WaterSettings.ClipmapGeomorphBand"/> at 0 byte-identical to 16.12.0.
        /// </para>
        /// </summary>
        /// <param name="i">Ring index on X, 0..<paramref name="ringCells"/>.</param>
        /// <param name="j">Ring index on Z, 0..<paramref name="ringCells"/>.</param>
        /// <param name="ringCells">Cells per side, already <see cref="ClampRingCells"/>ed.</param>
        /// <param name="band">Fraction of the ring's half-width the ramp spans, 0..1. At 1 the whole level morphs;
        /// at 0.5 a RING morphs entirely (a ring's drawn extent starts at half its half-width, where its hole
        /// ends) and level 0 morphs its outer half.</param>
        public static float MorphWeight(int i, int j, int ringCells, float band)
        {
            int half = ringCells / 2;
            if (half <= 0) return 0f;
            int r = Math.Max(Math.Abs(i - half), Math.Abs(j - half));
            if (r >= half) return 1f;
            float b = Math.Clamp(band, 0f, 1f);
            if (b <= 0f) return 0f;
            float inner = half * (1f - b);
            return Math.Clamp((r - inner) / (half - inner), 0f, 1f);
        }

        /// <summary>
        /// Which two nodes of the NEXT ring out's lattice a vertex at ring indices
        /// (<paramref name="i"/>, <paramref name="j"/>) sits between, as an index offset from it. <c>(0, 0)</c>
        /// means the vertex is itself on the coarse lattice.
        /// <para>
        /// A vertex is on the coarse lattice exactly when both indices are even: the ring's own origin is a
        /// multiple of twice its cell size and the coarse origin a multiple of four times it, so their difference
        /// is a whole number of coarse cells, and <c>ringCells</c> being a multiple of 4 makes the half-offset
        /// even too. The odd/odd case is the coarse quad's CENTRE and takes the anti-diagonal, which is the
        /// diagonal the index builders' <c>(i0, i2, i1) / (i1, i2, i3)</c> triangulation actually draws.
        /// </para>
        /// </summary>
        public static (int I, int J) CoarseNeighbourOffset(int i, int j)
        {
            bool oddI = (i & 1) != 0, oddJ = (j & 1) != 0;
            if (!oddI && !oddJ) return (0, 0);
            if (oddI && !oddJ) return (1, 0);
            if (!oddI) return (0, 1);
            return (1, -1);
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
        /// <param name="geomorphBand"><see cref="WaterSettings.ClipmapGeomorphBand"/>: the fraction of each ring's
        /// half-width over which it morphs toward the next ring out. 0 restores the pre-geomorph grid exactly (the
        /// boundary stitch and nothing else).</param>
        /// <param name="vertices">Receives the vertex block.</param>
        /// <param name="indices">Receives the triangle-list indices.</param>
        /// <param name="indexCount">Receives the index count actually written.</param>
        /// <param name="renderOrigin">Camera-relative render origin. Written positions come out in the RENDER
        /// frame, reduced by it.
        /// <para>
        /// <b>Everything above it is decided in absolute world space, and that ordering is the world lock.</b> The
        /// plane, the focus, the per-level snap and the stitch neighbours are all resolved on absolute
        /// coordinates, so the lattice a vertex lands on is a function of the world and of nothing else. Snapping
        /// in the RENDER frame instead would re-quantize every ring the moment the render origin rebased: the same
        /// world position would round to a different lattice node, every vertex would jump, and the surface would
        /// be resampled - which is the exact artifact this grid exists to remove, reintroduced by the fix for a
        /// different problem. <see cref="WaterClipmapVertex.Coarse"/> is a difference and
        /// <see cref="WaterClipmapVertex.Cell"/> and <see cref="WaterClipmapVertex.Morph"/> are scalars, so none of
        /// them needs reducing; the shader adds the origin
        /// back to recover the absolute position it samples the cascades at.
        /// </para>
        /// </param>
        public static int Build(in WaterPlane plane, float focusX, float focusZ, float baseCell, int ringCells,
            int levels, float geomorphBand, Span<WaterClipmapVertex> vertices, Span<uint> indices,
            out int indexCount, Vector3 renderOrigin = default)
        {
            int n = ringCells;
            int stride = n + 1;
            int perLevel = stride * stride;
            float cell = MathF.Max(baseCell, 1e-4f);
            levels = Math.Clamp(levels, 1, MaxLevels);

            // Everything below this point is in the RENDER frame, and the reduction happens exactly here: on the
            // ring ORIGINS and the plane's centre, never on a per-vertex position.
            //
            // That ordering is a precision requirement, not a preference. A ring origin is a whole multiple of
            // 2 * cellSize and the render origin is a whole multiple of the 128 m frame grid, so both are exact
            // integers in float32 out to 2^24 and their difference is exact. A per-vertex ABSOLUTE position is
            // neither: at 100 km, `origin + (i - half) * cell` has already rounded to the ~8 mm float lattice
            // before any subtraction could recover it, so reducing at that point would re-quantize the grid with
            // distance - the world lock would hold in metres and fail in millimetres, which is precisely the
            // defect camera-relative rendering exists to remove.
            float centerX = plane.CenterX - renderOrigin.X, centerZ = plane.CenterZ - renderOrigin.Z;
            float minX = centerX - plane.HalfExtentX, maxX = centerX + plane.HalfExtentX;
            float minZ = centerZ - plane.HalfExtentZ, maxZ = centerZ + plane.HalfExtentZ;
            float surfaceY = plane.SurfaceY - renderOrigin.Y;

            Span<float> originX = stackalloc float[MaxLevels];
            Span<float> originZ = stackalloc float[MaxLevels];
            for (int l = 0; l < levels; l++)
            {
                float c = CellSize(cell, l);
                // Snapped on the ABSOLUTE focus, so the lattice is a function of the world and a rebase cannot
                // move it; reduced immediately, so nothing downstream ever forms a large coordinate.
                originX[l] = SnapOrigin(focusX, c) - renderOrigin.X;
                originZ[l] = SnapOrigin(focusZ, c) - renderOrigin.Z;
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
                    for (int i = 0; i <= n; i++)
                    {
                        bool onBoundary = i == 0 || i == n || j == 0 || j == n;

                        float wx = Math.Clamp(ox + (i - half) * c, minX, maxX);
                        float wz = Math.Clamp(oz + (j - half) * c, minZ, maxZ);
                        Vector2 toCoarse = Vector2.Zero;
                        float morph = 0f;
                        float bandCell = c;

                        // The outermost level has no coarser neighbour, so it never morphs and never stitches.
                        if (hasCoarser)
                        {
                            morph = MorphWeight(i, j, n, geomorphBand);
                            if (morph > 0f)
                            {
                                (int di, int dj) = CoarseNeighbourOffset(i, j);
                                if (di != 0 || dj != 0)
                                {
                                    // The two coarse nodes, each clamped into the plane the same way the vertex
                                    // itself is, so a rectangle edge degrades the offset to zero rather than
                                    // sampling outside the water body.
                                    float lox = Math.Clamp(ox + (i - di - half) * c, minX, maxX);
                                    float hix = Math.Clamp(ox + (i + di - half) * c, minX, maxX);
                                    float loz = Math.Clamp(oz + (j - dj - half) * c, minZ, maxZ);
                                    float hiz = Math.Clamp(oz + (j + dj - half) * c, minZ, maxZ);
                                    toCoarse = new Vector2((hix - lox) * 0.5f, (hiz - loz) * 0.5f);
                                    // On the BOUNDARY the vertex is moved onto the coarse segment's midpoint, so
                                    // the two taps straddle it symmetrically and the seam is exact rather than
                                    // nearly exact. Inside the ring it keeps its own lattice position: the morph
                                    // is a blend of evaluations, not a displacement of the grid.
                                    if (onBoundary) { wx = (lox + hix) * 0.5f; wz = (loz + hiz) * 0.5f; }
                                }
                                // Band-limit spacing, morphed. Written as the exact endpoints at 0 and 1 rather
                                // than through one lerp, so an un-morphed vertex carries its own cell size and a
                                // fully morphed one the coarse size, bit for bit.
                                bandCell = morph >= 1f ? coarse : c + (coarse - c) * morph;
                            }
                        }

                        // wx/wz are already render-relative (the origins were reduced above), so this is a plain
                        // write: no large intermediate is ever formed per vertex.
                        vertices[written++] = new WaterClipmapVertex
                        {
                            Position = new Vector3(wx, surfaceY, wz),
                            Coarse = toCoarse,
                            Cell = bandCell,
                            Morph = morph,
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
