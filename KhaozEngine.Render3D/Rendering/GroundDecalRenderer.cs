using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the queued <see cref="GroundDecal"/>s into the lit color attachment + read-only scene depth
    /// (ColorDepthFB), sampling the linear depth to reconstruct each pixel's surface world position and painting the
    /// decal's analytic shape onto the ground/terrain. Runs after the model+beam passes and before the post chain, so
    /// decals are occluded by geometry (the read-only depth test rejects no-geometry background, the Y-band gate keeps
    /// shapes off vertical faces) and flow through quantize/blit.
    /// <para>
    /// FOUR PIPELINES: {alpha, additive} x {Greater, Equal}. The Greater pair is the base pass and paints every decal
    /// on GEOMETRY pixels. The Equal pair is its exact complement and paints only the
    /// <see cref="GroundDecal.VoidFallback"/>-flagged subset, on BACKGROUND pixels, projecting onto the decal's own
    /// horizontal plane so an overhanging shape reads over the void instead of truncating at the geometry's edge. A
    /// flagged decal is therefore drawn TWICE, once per pass, which is safe because the two depth tests partition the
    /// screen with no overlap and no gaps. Zero-neutral: with no flagged decals the instance bytes are identical, the
    /// void run list is empty, and the Equal pipelines are never bound.
    /// </para>
    /// </summary>
    /// <remarks>
    /// BATCHED + FOOTPRINT-BOUNDED. Consecutive decals of the same blend are coalesced into runs (see
    /// <see cref="CoalesceDecalRuns"/>) and each run is a SINGLE instanced draw - a boss fight with many AoEs, or
    /// blob-shadow mode with many characters, no longer costs one full-viewport draw per decal. Per-decal parameters
    /// live in a per-instance vertex ATTRIBUTE (the <see cref="DecalInstance"/> stream), consumed directly by the
    /// vertex shader and passed to the fragment stage - never used to index a buffer, the Metal-safe instancing
    /// invariant that <see cref="ModelRenderer"/>'s rigid instancing proves in production. Each instance rasterizes a
    /// screen-space QUAD covering the decal's projected ground footprint (<see cref="TryComputeScreenRect"/>) instead
    /// of a full-viewport triangle, so fill cost scales with decal area, not viewport area times decal count. A decal
    /// whose footprint straddles the camera (a corner at/behind the eye plane) falls back to a full-screen quad, so it
    /// is never clipped. Coalescing preserves submission order (a blend change starts a new run), and instances within
    /// a run rasterize in index order, so overlapping decals still composite in the order they were queued.
    /// </remarks>
    internal sealed partial class GroundDecalRenderer : IDisposable
    {
        /// <summary>Per-instance decal attributes, matching the <c>I*</c> inputs of <see cref="ShaderSources.DecalVert"/>
        /// (12 x vec4 = 192 bytes, every member 16-byte aligned). One entry per queued decal, streamed into the
        /// instance vertex buffer each frame.</summary>
        public struct DecalInstance
        {
            public Vector4 ScreenRect;    // ndc footprint rect (minX, minY, maxX, maxY)
            public Vector4 Center;        // xyz center, w = rotation
            public Vector4 Size;
            public Vector4 Fill;
            public Vector4 Outline;
            public Vector4 Params;        // x=edge, y=fillFraction, z=flashAdd, w=shapeIndex
            public Vector4 Gate;          // x=groundY, y=yTol, z=maxStep, w=featherWidth
            public Vector4 PatternP;      // x=pattern index, y=speed (cycles/s), z=cells per world unit, w=interiorDim
            public Vector4 Energy;        // x=rimGlow, y=sweepGlow, z=sparkle, w=runner
            public Vector4 Extra;         // x=baseFill, y=voidPath (0=depth-reconstruct, 1=plane-project), z=voidDim,
                                          // w=wantsFallback (geometry-pass instance of a VoidFallback decal). NOT reserved.
            public Vector4 Accent;        // MoltenCracks hot colour (rgb = crack glow tint, a = crack alpha); zero otherwise
            public Vector4 Misc;          // x=patternParam (pattern-owned), y=edgeErosion (0..1), z/w reserved (zero)
        }

        /// <summary>The single per-frame uniform block for the decal pass (Frame, set 0 binding 2). The retired
        /// Veldrid Metal backend mis-bound a second UBO, so the RAW inverse view-projection and the time/quality
        /// value were folded into this one block. #604 lifted that rule, and the block is still everything this
        /// pass reads. Mirrors <see cref="BeamRenderer"/>'s FrameUniforms. 80 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct FrameUniforms
        {
            public Matrix4x4 InvViewProj;   // RAW (un-clip-corrected), matching Camera.ScreenToRay picking
            public Vector4 TimeQ;           // x = effect time seconds, y = quality (1 full / 0 reduced), z = maxRgb ceiling, w = reject dynamic geometry (1 = discard skinned-tagged pixels)
        }

        /// <summary>A maximal run of consecutive queued decals sharing one blend, drawn as one instanced call.</summary>
        internal readonly struct DecalRun
        {
            public readonly DecalBlend Blend;
            public readonly int Start;
            public readonly int Count;
            public DecalRun(DecalBlend blend, int start, int count) { Blend = blend; Start = start; Count = count; }
        }

        // The full-screen NDC quad the vertex shader mixes to when a decal falls back (footprint straddles the camera):
        // exactly [-1,1] in x and y, so the two-triangle quad covers every on-screen pixel like the old fullscreen path.
        static readonly Vector4 FullScreenRect = new(-1f, -1f, 1f, 1f);

        // A small NDC margin added around every computed footprint rect: cheap insurance against float rounding in the
        // corner projection / rasterization so a boundary pixel the fullscreen path would have painted is never clipped.
        const float NdcMargin = 0.02f;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        // Greater at z=1: every decal, on GEOMETRY pixels. Rebuilt by SetOutputs when the MRT sample count (MSAA) changes.
        IGpuPipeline _alphaPipe, _additivePipe;
        // Equal at z=1: the exact complement, on BACKGROUND pixels. Bound only when a flagged decal is in the frame.
        IGpuPipeline _voidAlphaPipe, _voidAdditivePipe;
        readonly List<IDisposable> _retired = new();
        readonly IGpuBuffer _frameUbo;            // FrameSlotCount 256-byte slots, one per pass (see GroundDecalRenderer.FrameSlots.cs)
        IGpuBuffer? _instances;                   // per-instance attribute stream, grown geometrically (old one retired)
        int _capacity;
        DecalInstance[] _packed = Array.Empty<DecalInstance>();   // reused CPU scratch, sized with the buffer
        readonly List<DecalRun> _runs = new();        // base pass, over every decal
        readonly List<DecalRun> _voidRuns = new();    // void pass, over the flagged subset only (empty = zero extra draws)
        IGpuResourceSet? _set;
        RenderResources? _bound;
        int _boundGen;

        static readonly uint InstanceStride = (uint)Unsafe.SizeOf<DecalInstance>();

        /// <summary>Parity/profiling seam: when set, every decal uses the full-screen quad instead of its computed
        /// footprint rect - i.e. the pre-bounding full-viewport coverage (still batched/instanced). The footprint
        /// bounding is pixel-neutral (the fullscreen path discarded every out-of-footprint pixel anyway), so a bounded
        /// render must match a forced-fullscreen render. A GPU test flips this to prove exactly that, and profilers can
        /// use it to isolate the fill-reduction win. Off by default, so production always bounds the fill.</summary>
        internal bool ForceFullscreenQuads;

        public GroundDecalRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.DecalVert, ShaderSources.DecalFrag);
            // Still ONE uniform buffer (the Metal invariant when this was written, retired by #604), and the extra TEXTURE is fine. NormalTex is appended last so
            // the existing bindings do not renumber. Only the void-fallback path reads it, to reject a near-vertical
            // face as "not this decal's ground" - the Y band alone cannot tell the top of a cliff from a terrain dip.
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment, dynamic: true),
                new GpuResourceLayoutElement("NormalTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment)));
            _frameUbo = f.CreateBuffer(new GpuBufferDescription(FrameUboBytes, GpuBufferUsage.UniformBuffer));
            BuildPipelines(f, colorOutput);
        }

        /// <summary>Rebuild the pipelines for a new colour-target output description (e.g. the MRT became multisampled
        /// for MSAA - a pipeline's sample count must match its framebuffer). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription colorOutput)
        {
            _alphaPipe.Dispose(); _additivePipe.Dispose();
            _voidAlphaPipe.Dispose(); _voidAdditivePipe.Dispose();
            BuildPipelines(_gd.Factory, colorOutput);
        }

        /// <summary>The four pipelines: {alpha, additive} x {Greater = geometry, Equal = background}. The Equal pair is
        /// created unconditionally but bound only when the frame carries a flagged decal.</summary>
        [MemberNotNull(nameof(_alphaPipe), nameof(_additivePipe), nameof(_voidAlphaPipe), nameof(_voidAdditivePipe))]
        void BuildPipelines(IGpuResourceFactory f, GpuOutputDescription colorOutput)
        {
            _alphaPipe = Pipe(f, colorOutput, GpuBlendAttachment.AlphaBlend, GpuComparison.Greater);
            _additivePipe = Pipe(f, colorOutput, GpuBlendAttachment.Additive, GpuComparison.Greater);
            _voidAlphaPipe = Pipe(f, colorOutput, GpuBlendAttachment.AlphaBlend, GpuComparison.Equal);
            _voidAdditivePipe = Pipe(f, colorOutput, GpuBlendAttachment.Additive, GpuComparison.Equal);
        }

        IGpuPipeline Pipe(IGpuResourceFactory f, GpuOutputDescription outputs, GpuBlendAttachment blend, GpuComparison depth) =>
            f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { blend },
                // Read-only depth test on the far-plane quad (DecalVert emits z=1), one of an exact complementary pair:
                //   Greater - stored depth is NEARER than the far plane, i.e. scene GEOMETRY only (background rejected).
                //   Equal   - stored depth still EQUALS the cleared far plane, i.e. BACKGROUND only (geometry rejected),
                //             the same selection SkyRenderer and StarfieldRenderer use for their background passes.
                // Together they partition the screen with no overlap and no gaps, which is what lets a flagged decal be
                // drawn twice (once per pass) without any double-blending. GreaterEqual would be wrong for the void
                // pipeline: 1 >= any storedZ, so it would paint over ALL geometry. No depth write in either, so the
                // scene depth is untouched for any later pass.
                DepthStencil = new GpuDepthStencilState(depthTestEnabled: true, depthWriteEnabled: false, depth),
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                // One instance-rate vertex stream carrying the twelve per-decal vec4 attributes (locations 0..11, no holes).
                VertexLayouts = new List<GpuVertexLayoutDescription>
                {
                    new GpuVertexLayoutDescription(
                        stride: InstanceStride,
                        instanceStepRate: 1,
                        elements: new[]
                        {
                            new GpuVertexElement("IScreenRect", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("ICenter", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("ISize", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IFill", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IOutline", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IParams", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IGate", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IPattern", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IEnergy", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IExtra", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IAccent", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IMisc", GpuVertexElementFormat.Float4),
                        }),
                },
                Outputs = outputs,
            });

        void BindTargets(RenderResources res)
        {
            // Rebuild only when the targets change (the frame UBO handle is fixed for the renderer's life, so the set
            // does not churn on instance-buffer regrowth the way the old dynamic-offset set did).
            if (_set != null && ReferenceEquals(_bound, res) && res.Generation == _boundGen) return;
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, res.DepthColorTex,
                _gd.PointSampler, new GpuBufferRange(_frameUbo, 0, FramePayloadBytes), res.NormalTex));
            _bound = res; _boundGen = res.Generation;
        }

        /// <summary>Ensure the instance buffer + CPU scratch hold at least <paramref name="decalCount"/> entries,
        /// growing geometrically. A regrown buffer retires the old one (a prior frame's command list may still read
        /// it) and is freed in <see cref="Dispose"/>.</summary>
        void EnsureCapacity(int decalCount)
        {
            if (_instances != null && _capacity >= decalCount) return;
            if (_instances != null) _retired.Add(_instances);
            _capacity = Math.Max(decalCount, _capacity == 0 ? 8 : _capacity * 2);
            _instances = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)(_capacity * (int)InstanceStride), GpuBufferUsage.VertexBuffer));
            if (_packed.Length < _capacity) _packed = new DecalInstance[_capacity];
        }

        /// <summary>Pure: pack a decal + its computed NDC footprint rect into the per-instance attribute struct. This
        /// is the GEOMETRY-pass instance. The void lanes stay ZERO for an unflagged decal, whatever else is set on it,
        /// so its bytes are exactly what they were before the fallback existed - the zero-neutral contract.</summary>
        public static DecalInstance PackInstance(in GroundDecal d, in Vector4 screenRect) => new()
        {
            ScreenRect = screenRect,
            Center = new Vector4(d.Center, d.Rotation),
            Size = d.Size,
            Fill = d.FillColor,
            Outline = d.OutlineColor,
            Params = new Vector4(d.EdgeThickness, d.FillFraction, d.FlashAdd, (int)d.Shape),
            Gate = new Vector4(d.Center.Y, d.YTolerance, d.MaxStep, d.FeatherWidth),
            PatternP = new Vector4((int)d.Pattern, d.PatternSpeed, d.PatternScale, d.InteriorDim),
            Energy = new Vector4(d.RimGlow, d.SweepGlow, d.Sparkle, d.Runner),
            // y = 0: this instance reads real geometry. w = 1 asks the geometry path to fall back to the decal's own
            // plane where the surface it finds is OUT of the Y band but the plane is nearer than that surface (an
            // overhanging ring hanging in front of the cliff below it). Both void lanes are gated on the flag, so an
            // unflagged decal packs (baseFill, 0, 0, 0) exactly as it always did, even if VoidDim was left set.
            Extra = new Vector4(d.BaseFill, 0f, VoidDimOf(d), d.VoidFallback ? 1f : 0f),
            // Accent + PatternParam are read only by MoltenCracks, so any other pattern packs them as zero (the
            // VoidDim precedent: authored-but-unused values never move a non-opting decal's bytes). EdgeErosion is
            // pattern-agnostic and packs whenever set. The shader gates on > 0 so a zero lane is arithmetically inert.
            Accent = d.Pattern == DecalFillPattern.MoltenCracks ? (Vector4)d.AccentColor : Vector4.Zero,
            Misc = new Vector4(
                d.Pattern == DecalFillPattern.MoltenCracks ? d.PatternParam : 0f,
                Math.Clamp(d.EdgeErosion, 0f, 1f), 0f, 0f),
        };

        /// <summary>The clamped void dim, or 0 when the decal did not opt in (so an authored-but-unused VoidDim can
        /// never move an unflagged decal's bytes).</summary>
        static float VoidDimOf(in GroundDecal d) => d.VoidFallback ? Math.Clamp(d.VoidDim, 0f, 1f) : 0f;

        /// <summary>Pure: pack a flagged decal's BACKGROUND instance - byte-for-byte <see cref="PackInstance"/> except
        /// the Extra lane, which raises the background marker (y=1) the fragment shader branches on. Delegates so the
        /// base packing stays the single source of truth: a new per-decal field only ever has to be added in one
        /// place. The caller passes the FLAT screen rect (<see cref="TryComputeScreenRectFlat"/>), not the Y-band one.
        /// The fallback-request lane (w) is cleared: this instance IS the plane path, it does not ask for it.</summary>
        public static DecalInstance PackVoidInstance(in GroundDecal d, in Vector4 screenRect)
        {
            DecalInstance inst = PackInstance(d, screenRect);
            inst.Extra = new Vector4(d.BaseFill, 1f, VoidDimOf(d), 0f);
            return inst;
        }

        /// <summary>World-space bounding radius of a decal's painted shape about its <see cref="GroundDecal.Center"/>
        /// (rotation-invariant, since every shape rotates about +Y): the max radial extent any painted texel can
        /// reach. Pure + headless-testable. Used to size the footprint AABB in <see cref="TryComputeScreenRect"/>.</summary>
        internal static float BoundingRadius(in GroundDecal d) => d.Shape switch
        {
            DecalShape.Circle => d.Size.X,                 // radius
            DecalShape.Ring => d.Size.Y,                   // outer radius
            DecalShape.Beam => 2f * d.Size.X + d.Size.Y,   // origin at one end: spans 2*halfLength along +x, +/-halfWidth
            DecalShape.Cone => d.Size.X,                   // range
            DecalShape.Arc => d.Size.X + d.Size.Y,         // radius + half band width
            _ => MathF.Max(d.Size.X, d.Size.Y),
        };

        /// <summary>
        /// Compute the screen-space NDC rectangle covering a decal's projected ground footprint, given the
        /// GPU-clip-corrected world-&gt;clip matrix <paramref name="clipVp"/> (so the emitted quad lands on the same
        /// pixels the geometry does, on every backend). The footprint is the world AABB of the shape's bounding radius
        /// (inflated by the outline/AA band) over the decal's Y gate band. Its 8 corners are projected and their NDC
        /// bounding box (plus a small margin, clamped to the screen) is the quad rect. Returns <c>false</c> when any
        /// corner is at/behind the eye plane (<c>w &lt;= eps</c>) - the convex-hull screen-bound no longer holds, so
        /// the caller falls back to a full-screen quad. Pure over its matrix input (no GPU), headless-testable.
        /// </summary>
        internal static bool TryComputeScreenRect(in GroundDecal d, in Matrix4x4 clipVp, float ndcMargin, out Vector4 rect)
        {
            float minY = d.Center.Y - d.YTolerance, maxY = d.Center.Y + d.MaxStep;
            if (maxY < minY) (minY, maxY) = (maxY, minY);   // a negative-authored band still yields a valid AABB
            return TryComputeScreenRectCore(d, clipVp, ndcMargin, minY, maxY, out rect);
        }

        /// <summary>The VOID pass's footprint rect: the same AABB FLATTENED onto the decal's own plane
        /// (minY = maxY = <see cref="GroundDecal.Center"/>.Y), which is exactly where the void path projects. Tighter
        /// than, and correct where the Y-gate band is not: the plane projection never leaves y = Center.Y, so the
        /// gate band would only inflate the quad with pixels the shader discards anyway. Same camera-straddle
        /// fullscreen fallback contract as <see cref="TryComputeScreenRect"/>. Pure, headless-testable.</summary>
        internal static bool TryComputeScreenRectFlat(in GroundDecal d, in Matrix4x4 clipVp, float ndcMargin, out Vector4 rect)
            => TryComputeScreenRectCore(d, clipVp, ndcMargin, d.Center.Y, d.Center.Y, out rect);

        /// <summary>Shared core of the two rect builders, parameterized on the AABB's Y span.</summary>
        static bool TryComputeScreenRectCore(in GroundDecal d, in Matrix4x4 clipVp, float ndcMargin, float minY, float maxY, out Vector4 rect)
        {
            rect = FullScreenRect;
            float r = BoundingRadius(d) + 2f * MathF.Max(d.EdgeThickness, 1e-4f);   // include the outline/AA band
            float minX = d.Center.X - r, maxX = d.Center.X + r;
            float minZ = d.Center.Z - r, maxZ = d.Center.Z + r;

            float nx0 = float.MaxValue, ny0 = float.MaxValue, nx1 = float.MinValue, ny1 = float.MinValue;
            const float wEps = 1e-4f;
            for (int c = 0; c < 8; c++)
            {
                float x = (c & 1) == 0 ? minX : maxX;
                float y = (c & 2) == 0 ? minY : maxY;
                float z = (c & 4) == 0 ? minZ : maxZ;
                Vector4 clip = Vector4.Transform(new Vector4(x, y, z, 1f), clipVp);
                if (clip.W <= wEps) return false;   // straddles the camera: fall back to fullscreen (never clip a decal)
                float invW = 1f / clip.W;
                float ndcX = clip.X * invW, ndcY = clip.Y * invW;
                nx0 = MathF.Min(nx0, ndcX); ny0 = MathF.Min(ny0, ndcY);
                nx1 = MathF.Max(nx1, ndcX); ny1 = MathF.Max(ny1, ndcY);
            }
            nx0 -= ndcMargin; ny0 -= ndcMargin; nx1 += ndcMargin; ny1 += ndcMargin;
            nx0 = Math.Clamp(nx0, -1f, 1f); ny0 = Math.Clamp(ny0, -1f, 1f);
            nx1 = Math.Clamp(nx1, -1f, 1f); ny1 = Math.Clamp(ny1, -1f, 1f);
            rect = new Vector4(nx0, ny0, nx1, ny1);
            return true;
        }

        /// <summary>
        /// Coalesce <paramref name="decals"/> (in submission order) into <paramref name="runs"/>: each run is a maximal
        /// span of consecutive decals sharing one <see cref="DecalBlend"/> (the only thing that forces a pipeline /
        /// draw split). Submission order is preserved - a blend change starts a new run rather than globally grouping -
        /// so overlapping decals still composite in the order queued, mirroring the SpriteBatch run-coalescing pattern.
        /// Pure + headless-testable. <paramref name="runs"/> is Cleared and refilled.
        /// </summary>
        internal static void CoalesceDecalRuns(ReadOnlySpan<GroundDecal> decals, List<DecalRun> runs)
        {
            runs.Clear();
            if (decals.Length == 0) return;
            int start = 0;
            for (int i = 1; i <= decals.Length; i++)
            {
                bool boundary = i == decals.Length || decals[i].Blend != decals[start].Blend;
                if (boundary) { runs.Add(new DecalRun(decals[start].Blend, start, i - start)); start = i; }
            }
        }

        /// <summary>
        /// Coalesce the <see cref="GroundDecal.VoidFallback"/>-flagged SUBSET of <paramref name="decals"/> (in
        /// submission order) into <paramref name="runs"/>. Start indices address the void instances APPENDED after
        /// every base instance, so they begin at <paramref name="baseOffset"/> (= the decal count). Void slots are
        /// contiguous by construction, so - exactly like <see cref="CoalesceDecalRuns"/> - a run breaks only on a
        /// blend change and submission order is preserved. Unflagged decals contribute nothing, so a frame with none
        /// yields ZERO runs and therefore zero extra draws and zero Equal-pipeline binds: the zero-neutral contract.
        /// Pure + headless-testable. <paramref name="runs"/> is Cleared and refilled.
        /// </summary>
        internal static void CoalesceVoidRuns(ReadOnlySpan<GroundDecal> decals, int baseOffset, List<DecalRun> runs)
        {
            runs.Clear();
            int slot = baseOffset;
            for (int i = 0; i < decals.Length; i++)
            {
                if (!decals[i].VoidFallback) continue;
                DecalBlend blend = decals[i].Blend;
                if (runs.Count > 0 && runs[^1].Blend == blend)
                    runs[^1] = new DecalRun(blend, runs[^1].Start, runs[^1].Count + 1);
                else
                    runs.Add(new DecalRun(blend, slot, 1));
                slot++;
            }
        }

        /// <summary>Count the void-flagged decals in <paramref name="decals"/>: how many instances the void pass
        /// appends, and thus how far past the decal count the instance buffer must reach.</summary>
        internal static int CountVoidDecals(ReadOnlySpan<GroundDecal> decals)
        {
            int n = 0;
            for (int i = 0; i < decals.Length; i++) if (decals[i].VoidFallback) n++;
            return n;
        }

        /// <summary>Draw all queued decals into ColorDepthFB (lit color + read-only scene depth) as one instanced draw
        /// per blend run. <paramref name="timeSeconds"/> drives the animated noise + edge energy and
        /// <paramref name="quality"/> folds into the Frame UBO's quality lane (Reduced drops the second noise octave and
        /// the edge sparkle). <paramref name="hdr"/> raises the final-rgb clamp ceiling from 1.0 (LDR, bit-identical to
        /// the legacy chain) to the float16 max so the energy lanes can push telegraph cores over 1.0 and bloom. Caller
        /// guarantees the model pass is complete (depth written) and the framebuffer is free to rebind. Returns the
        /// number of GPU draw calls issued (= blend-run count), so the caller keeps its frame stats honest. No-op
        /// (returns 0) when empty.
        /// <para>
        /// <paramref name="pass"/> says WHICH of the frame's two decal passes this is, and decides two things
        /// together. It selects the frame UBO slot this pass's draws bind (see
        /// <c>GroundDecalRenderer.FrameSlots.cs</c>: the two passes used to share one range, which the native
        /// backends' uniform ring collapses onto the last write, issue #483). And it decides the GEOMETRY path's
        /// dynamic reject, which discards pixels the model pass tagged as dynamic/skinned (normal-target alpha ~0)
        /// so a ground decal never paints onto a character standing in its Y-band (issue #235).
        /// <see cref="FramePass.Main"/> rejects: it runs after the normal target is resolved.
        /// <see cref="FramePass.BlobShadow"/> does not: it runs before the skinned draws and resolves only depth, so
        /// the normal alpha it would read is not yet valid under MSAA (and blob shadows want no such reject anyway).
        /// With no dynamic geometry every geometry pixel keeps alpha 1, so the reject never fires and the render is
        /// byte-identical.
        /// </para></summary>
        public int Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 viewProj, float timeSeconds, GroundDecalQuality quality, bool hdr, FramePass pass, ReadOnlySpan<GroundDecal> decals)
        {
            if (decals.Length == 0) return 0;
            bool rejectDynamicGeometry = pass == FramePass.Main;
            int voidCount = CountVoidDecals(decals);
            EnsureCapacity(decals.Length + voidCount);
            BindTargets(res);
            // Reconstruct with the RAW view-projection inverse (NOT GpuClip-corrected): the decal frag unprojects
            // screen->world like Camera.ScreenToRay picking, which is CPU/backend-independent. The clip-CORRECTED
            // matrix, by contrast, positions the footprint QUAD so it lands on the same pixels the geometry does.
            Matrix4x4.Invert(viewProj, out var inv);
            // maxRgb ceiling on TimeQ.z: 1.0 keeps the legacy clamp bit-identical, 65504.0 (float16 max) lets the HDR
            // chain carry over-range decal energy into the pre-tonemap bloom.
            float maxRgb = hdr ? 65504f : 1f;
            var frame = new FrameUniforms
            {
                InvViewProj = inv,
                TimeQ = new Vector4(timeSeconds, quality == GroundDecalQuality.Full ? 1f : 0f, maxRgb, rejectDynamicGeometry ? 1f : 0f),
            };
            // This pass's own slot, then the WHOLE mirror in one write, ahead of every draw that binds a slot of
            // it. The other pass's slot goes up again carrying the bytes it already held, so the upload is a no-op
            // for anything already recorded (see GroundDecalRenderer.FrameSlots.cs).
            PackFrameSlot(pass, in frame);
            UploadFrameSlots(cl);
            uint frameSlot = FrameSlotOffset(pass);
            Matrix4x4 clipVp = GpuClip.Correct(viewProj, _gd.Capabilities);

            for (int i = 0; i < decals.Length; i++)
            {
                Vector4 rect = (!ForceFullscreenQuads && TryComputeScreenRect(decals[i], clipVp, NdcMargin, out Vector4 r))
                    ? r : FullScreenRect;
                _packed[i] = PackInstance(decals[i], rect);
            }
            // Void instances are APPENDED after every base instance, never interleaved, so the base slice's bytes are
            // exactly what they were before this feature existed and an unflagged frame packs identically.
            int slot = decals.Length;
            for (int i = 0; i < decals.Length; i++)
            {
                if (!decals[i].VoidFallback) continue;
                Vector4 rect = (!ForceFullscreenQuads && TryComputeScreenRectFlat(decals[i], clipVp, NdcMargin, out Vector4 r))
                    ? r : FullScreenRect;
                _packed[slot++] = PackVoidInstance(decals[i], rect);
            }
            cl.UpdateBuffer(_instances!, 0, ((ReadOnlySpan<DecalInstance>)_packed).Slice(0, decals.Length + voidCount));

            CoalesceDecalRuns(decals, _runs);
            CoalesceVoidRuns(decals, decals.Length, _voidRuns);
            cl.SetFramebuffer(res.ColorDepthFB);
            cl.SetVertexBuffer(0, _instances!);
            foreach (var run in _runs)
            {
                cl.SetPipeline(run.Blend == DecalBlend.Additive ? _additivePipe : _alphaPipe);
                cl.SetGraphicsResourceSet(0, _set!, frameSlot);
                // 6 vertices (two-triangle quad) x run.Count instances. Base instance = run.Start selects this run's
                // slice of the shared instance buffer (the same base-instance path the model/shadow instanced draws use).
                cl.Draw(6, (uint)run.Count, 0, (uint)run.Start);
            }
            // Void pass: the Equal-at-far complement, over the flagged subset only. Empty for an unflagged frame, so
            // this loop binds nothing and issues nothing. The two passes are disjoint by hardware (Greater and Equal at
            // z=1 partition the screen), so running it second is bookkeeping, not a blend-order decision.
            foreach (var run in _voidRuns)
            {
                cl.SetPipeline(run.Blend == DecalBlend.Additive ? _voidAdditivePipe : _voidAlphaPipe);
                cl.SetGraphicsResourceSet(0, _set!, frameSlot);
                cl.Draw(6, (uint)run.Count, 0, (uint)run.Start);
            }
            return _runs.Count + _voidRuns.Count;
        }

        public void Dispose()
        {
            _set?.Dispose();
            _alphaPipe.Dispose(); _additivePipe.Dispose();
            _voidAlphaPipe.Dispose(); _voidAdditivePipe.Dispose();
            _layout.Dispose(); _shaders.Dispose();
            _frameUbo.Dispose();
            _instances?.Dispose();
            foreach (var r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
