using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the queued <see cref="GroundDecal"/>s into the lit color attachment + read-only scene depth
    /// (ColorDepthFB), sampling the linear depth to reconstruct each pixel's surface world position and painting the
    /// decal's analytic shape onto the ground/terrain. Runs after the model+beam passes and before the post chain, so
    /// decals are occluded by geometry (the read-only depth test rejects no-geometry background; the Y-band gate keeps
    /// shapes off vertical faces) and flow through quantize/blit. Two pipelines: alpha and additive.
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
    internal sealed class GroundDecalRenderer : IDisposable
    {
        /// <summary>Per-instance decal attributes, matching the <c>I*</c> inputs of <see cref="ShaderSources.DecalVert"/>
        /// (7 x vec4 = 112 bytes; every member 16-byte aligned). One entry per queued decal, streamed into the
        /// instance vertex buffer each frame.</summary>
        public struct DecalInstance
        {
            public Vector4 ScreenRect;    // ndc footprint rect (minX, minY, maxX, maxY)
            public Vector4 Center;        // xyz center, w = rotation
            public Vector4 Size;
            public Vector4 Fill;
            public Vector4 Outline;
            public Vector4 Params;        // x=edge, y=fillFraction, z=flashAdd, w=shapeIndex
            public Vector4 Gate;          // x=groundY, y=yTol, z=maxStep, w=0
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
        IGpuPipeline _alphaPipe, _additivePipe;   // rebuilt by SetOutputs when the MRT sample count (MSAA) changes
        readonly List<IDisposable> _retired = new();
        readonly IGpuBuffer _frameUbo;            // 64 bytes: the RAW inverse view-projection, shared by every decal
        IGpuBuffer? _instances;                   // per-instance attribute stream, grown geometrically (old one retired)
        int _capacity;
        DecalInstance[] _packed = Array.Empty<DecalInstance>();   // reused CPU scratch, sized with the buffer
        readonly List<DecalRun> _runs = new();
        IGpuResourceSet? _set;
        RenderResources? _bound;
        int _boundW, _boundH;

        static readonly uint InstanceStride = (uint)Unsafe.SizeOf<DecalInstance>();

        /// <summary>Parity/profiling seam: when set, every decal uses the full-screen quad instead of its computed
        /// footprint rect - i.e. the pre-bounding full-viewport coverage (still batched/instanced). The footprint
        /// bounding is pixel-neutral (the fullscreen path discarded every out-of-footprint pixel anyway), so a bounded
        /// render must match a forced-fullscreen render; a GPU test flips this to prove exactly that, and profilers can
        /// use it to isolate the fill-reduction win. Off by default, so production always bounds the fill.</summary>
        internal bool ForceFullscreenQuads;

        public GroundDecalRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.DecalVert, ShaderSources.DecalFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            _frameUbo = f.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer));
            _alphaPipe = Pipe(f, colorOutput, GpuBlendAttachment.AlphaBlend);
            _additivePipe = Pipe(f, colorOutput, GpuBlendAttachment.Additive);
        }

        /// <summary>Rebuild the pipelines for a new colour-target output description (e.g. the MRT became multisampled
        /// for MSAA - a pipeline's sample count must match its framebuffer). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription colorOutput)
        {
            _alphaPipe.Dispose(); _additivePipe.Dispose();
            var f = _gd.Factory;
            _alphaPipe = Pipe(f, colorOutput, GpuBlendAttachment.AlphaBlend);
            _additivePipe = Pipe(f, colorOutput, GpuBlendAttachment.Additive);
        }

        IGpuPipeline Pipe(IGpuResourceFactory f, GpuOutputDescription outputs, GpuBlendAttachment blend) =>
            f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { blend },
                // Read-only depth test: the far-plane quad (DecalVert emits z=1) passes Greater only where stored depth
                // is nearer than the far plane, i.e. only on scene geometry; background (cleared far) is rejected. No
                // depth write, so the scene depth is untouched for any later pass.
                DepthStencil = new GpuDepthStencilState(depthTestEnabled: true, depthWriteEnabled: false, GpuComparison.Greater),
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                // One instance-rate vertex stream carrying the seven per-decal vec4 attributes (locations 0..6).
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
                        }),
                },
                Outputs = outputs,
            });

        void BindTargets(RenderResources res)
        {
            // Rebuild only when the targets change (the frame UBO handle is fixed for the renderer's life, so the set
            // does not churn on instance-buffer regrowth the way the old dynamic-offset set did).
            if (_set != null && ReferenceEquals(_bound, res) && res.Width == _boundW && res.Height == _boundH) return;
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, res.DepthColorTex, _gd.PointSampler, _frameUbo));
            _bound = res; _boundW = res.Width; _boundH = res.Height;
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

        /// <summary>Pure: pack a decal + its computed NDC footprint rect into the per-instance attribute struct.</summary>
        public static DecalInstance PackInstance(in GroundDecal d, in Vector4 screenRect) => new()
        {
            ScreenRect = screenRect,
            Center = new Vector4(d.Center, d.Rotation),
            Size = d.Size,
            Fill = d.FillColor,
            Outline = d.OutlineColor,
            Params = new Vector4(d.EdgeThickness, d.FillFraction, d.FlashAdd, (int)d.Shape),
            Gate = new Vector4(d.Center.Y, d.YTolerance, d.MaxStep, 0f),
        };

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
        /// (inflated by the outline/AA band) over the decal's Y gate band; its 8 corners are projected and their NDC
        /// bounding box (plus a small margin, clamped to the screen) is the quad rect. Returns <c>false</c> when any
        /// corner is at/behind the eye plane (<c>w &lt;= eps</c>) - the convex-hull screen-bound no longer holds, so
        /// the caller falls back to a full-screen quad. Pure over its matrix input (no GPU), headless-testable.
        /// </summary>
        internal static bool TryComputeScreenRect(in GroundDecal d, in Matrix4x4 clipVp, float ndcMargin, out Vector4 rect)
        {
            rect = FullScreenRect;
            float r = BoundingRadius(d) + 2f * MathF.Max(d.EdgeThickness, 1e-4f);   // include the outline/AA band
            float minX = d.Center.X - r, maxX = d.Center.X + r;
            float minZ = d.Center.Z - r, maxZ = d.Center.Z + r;
            float minY = d.Center.Y - d.YTolerance, maxY = d.Center.Y + d.MaxStep;
            if (maxY < minY) (minY, maxY) = (maxY, minY);   // a negative-authored band still yields a valid AABB

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
        /// Pure + headless-testable; <paramref name="runs"/> is Cleared and refilled.
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

        /// <summary>Draw all queued decals into ColorDepthFB (lit color + read-only scene depth) as one instanced draw
        /// per blend run. Caller guarantees the model pass is complete (depth written) and the framebuffer is free to
        /// rebind. Returns the number of GPU draw calls issued (= blend-run count), so the caller keeps its frame
        /// stats honest. No-op (returns 0) when empty.</summary>
        public int Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 viewProj, ReadOnlySpan<GroundDecal> decals)
        {
            if (decals.Length == 0) return 0;
            EnsureCapacity(decals.Length);
            BindTargets(res);
            // Reconstruct with the RAW view-projection inverse (NOT GpuClip-corrected): the decal frag unprojects
            // screen->world like Camera.ScreenToRay picking, which is CPU/backend-independent. The clip-CORRECTED
            // matrix, by contrast, positions the footprint QUAD so it lands on the same pixels the geometry does.
            Matrix4x4.Invert(viewProj, out var inv);
            cl.UpdateBuffer(_frameUbo, 0, in inv);
            Matrix4x4 clipVp = GpuClip.Correct(viewProj, _gd.Capabilities);

            for (int i = 0; i < decals.Length; i++)
            {
                Vector4 rect = (!ForceFullscreenQuads && TryComputeScreenRect(decals[i], clipVp, NdcMargin, out Vector4 r))
                    ? r : FullScreenRect;
                _packed[i] = PackInstance(decals[i], rect);
            }
            cl.UpdateBuffer(_instances!, 0, ((ReadOnlySpan<DecalInstance>)_packed).Slice(0, decals.Length));

            CoalesceDecalRuns(decals, _runs);
            cl.SetFramebuffer(res.ColorDepthFB);
            cl.SetVertexBuffer(0, _instances!);
            foreach (var run in _runs)
            {
                cl.SetPipeline(run.Blend == DecalBlend.Additive ? _additivePipe : _alphaPipe);
                cl.SetGraphicsResourceSet(0, _set!);
                // 6 vertices (two-triangle quad) x run.Count instances; base instance = run.Start selects this run's
                // slice of the shared instance buffer (the same base-instance path the model/shadow instanced draws use).
                cl.Draw(6, (uint)run.Count, 0, (uint)run.Start);
            }
            return _runs.Count;
        }

        public void Dispose()
        {
            _set?.Dispose();
            _alphaPipe.Dispose(); _additivePipe.Dispose();
            _layout.Dispose(); _shaders.Dispose();
            _frameUbo.Dispose();
            _instances?.Dispose();
            foreach (var r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
