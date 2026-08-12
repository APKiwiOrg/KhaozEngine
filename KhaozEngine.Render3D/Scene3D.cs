using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>Blend mode for <see cref="Scene3D.DrawBillboard(System.Numerics.Vector3, float, KhaozEngine.Primitives.Color, KhaozEngine.Render3D.BillboardBlend)"/>: standard <see cref="Alpha"/> transparency,
    /// or <see cref="Additive"/> (source-alpha/one) for glowy accumulation (sparks, muzzle flashes).</summary>
    public enum BillboardBlend { Alpha, Additive }

    /// <summary>
    /// A drawable 3D scene: an <see cref="IsoCamera3D"/>, a set of uploaded meshes, a per-frame instance queue,
    /// and the pixel post chain. Load meshes once with <see cref="LoadMesh(KhaozEngine.Render3D.GltfMesh)"/>; each frame call
    /// <see cref="Begin"/>, queue instances with <see cref="Draw(KhaozEngine.Render3D.MeshHandle, System.Numerics.Matrix4x4)"/>, then have the surface/host render. Owns its
    /// GPU resources (via the KhaozEngine.Gpu seam) but records into a caller-supplied command list (see
    /// <see cref="Render3DSurface"/>); the public surface stays backend-free.
    /// </summary>
    public sealed partial class Scene3D : IDisposable
    {
        readonly IGpuDevice _gd;
        readonly GpuOutputDescription _targetOutput;
        readonly ModelRenderer _model;
        readonly PixelPostProcess _post;
        readonly LineRenderer _lines;
        readonly Rendering.DepthLineRenderer _depthLines;
        readonly FillRenderer _fills;
        readonly BillboardRenderer _billboards;
        readonly TransitionRenderer _transitions;
        readonly TexturedBillboardRenderer _texBillboards;
        readonly BeamRenderer _beams;
        readonly TrailRenderer _trails;
        readonly Rendering.GroundDecalRenderer _decalRenderer;
        readonly Rendering.ParticleRenderer _particleRenderer;
        readonly Rendering.DistortionRenderer _distortionRenderer;
        readonly Rendering.SkyRenderer _sky;
        readonly Rendering.StarfieldRenderer _starfield;
        readonly Rendering.WaterRenderer _water;
        readonly Rendering.OverlayMeshRenderer _overlayMeshes;
        readonly RenderResources _res;
        // Slot-indexed GPU mesh storage parallel to _slots; a freed slot's entry is null until reused.
        readonly List<Mesh?> _meshes = new();
        readonly MeshSlotMap _slots = new();
        // Bound once here (not rebuilt per call) so RenderInternal's per-frame ApplyAlphaCutoffs costs no closure
        // allocation despite reading instance state (_slots, _meshes) a static delegate cannot see (issue #374).
        // The method group capture happens once for this Scene3D's lifetime, see AlphaCutoffFor near ApplyAlphaCutoffs.
        readonly Func<MeshHandle, float> _alphaCutoffLookup;
        readonly RetiredResourcePool _retired;   // mesh buffers freed mid-life, destroyed behind one drain per frame
        // Loaded albedo textures, indexed by TextureHandle.Index. Shared across meshes; disposed in Dispose.
        readonly List<IGpuTexture?> _textures = new();   // a slot is nulled by UnloadTexture (handle stays stable, not recycled)
        // Loaded splat-terrain materials, indexed by SplatMaterialHandle.ListIndex. Each owns its two texture
        // arrays + params UBO + resource set; shared across meshes; disposed in Dispose / UnloadSplatMaterial.
        readonly List<SplatMaterialEntry?> _splatMaterials = new();
        // Per-texture billboard resource sets, parallel to _textures (ListIndex), created lazily the first time a
        // texture is used for a textured billboard. Disposed in Dispose.
        readonly List<IGpuResourceSet?> _texBillboardSets = new();
        readonly SceneInstances _instances = new();
        // Per-frame dynamic point lights, cleared each Begin() like the instance queue. The host adds them
        // (already N-nearest-culled); the renderer clamps to MaxPointLights and zero-fills the rest.
        readonly List<ModelRenderer.PointLightData> _lights = new();
        readonly List<LineRenderer.LineVertex> _lineVerts = new();
        // Depth-tested debug wire volumes (DebugDepthMode.DepthTested). Drawn into ColorDepthFB before the post
        // chain so scene geometry occludes them. The always-on-top variant feeds _lineVerts instead. Cleared each
        // Begin() like _lineVerts.
        readonly List<LineRenderer.LineVertex> _depthLineVerts = new();
        readonly List<FillRenderer.FillVertex> _fillVerts = new();
        readonly List<GroundDecal> _decals = new();
        // Per-frame blob-shadow requests (ShadowMode.Blob). Cleared each Begin() like the decal queue. When the
        // resolved shadow tier is Blob, each request is turned into a dark Circle GroundDecal at render time and
        // drawn through the ground-decal projection path, so a blob reuses the same depth-reconstructed grounding.
        readonly List<ShadowBlob> _shadowBlobs = new();
        // Reused scratch for the blobs-as-decals so the per-frame conversion allocates nothing.
        readonly List<GroundDecal> _shadowDecals = new();
        // Shadow depth-pass dirty-skip state (efficiency): the 2048^2 light-space depth map is a persistent GPU
        // texture, so when nothing shadow-relevant changed since the last RENDERED pass the pass is skipped and the
        // prior map is reused (never cleared) - a mostly-static scene stops repainting every caster into it every
        // frame. Kept from the last rendered pass, compared against this frame in RenderInternal. The caster
        // signature buffers are swapped (not copied) when a dirty pass commits, so the check stays allocation-free.
        bool _shadowPassRendered;             // a real depth pass has rendered since construction (map holds valid content)
        bool _shadowPassSkippedLastFrame;     // the last rendered frame reused the prior depth map (public signal below)
        ShadowPassDiagnostics _lastShadowPassDiagnostics;
        // Per-cascade CPU (pre-GPU-clip-correct) fit matrices for THIS frame (0.._cascadeCount-1 valid). The GPU-clip
        // corrected RECEIVER matrices + the column-transformed DEPTH matrices are derived from them each frame.
        readonly Matrix4x4[] _cascadeCpuVps = new Matrix4x4[ShadowSettings.MaxCascades];
        readonly float[] _cascadeRadii = new float[ShadowSettings.MaxCascades];       // fitted slice-sphere radii (texel world size source)
        readonly FrustumPlanes[] _shadowFrustums = new FrustumPlanes[ShadowSettings.MaxCascades];
        readonly Vector3[] _frustumCornersScratch = new Vector3[8];
        readonly Matrix4x4[] _cascadeReceiverVps = new Matrix4x4[ShadowSettings.MaxCascades];  // GPU-clip-corrected, receiver-sampled
        readonly Matrix4x4[] _cascadeDepthVps = new Matrix4x4[ShadowSettings.MaxCascades];      // receiver * atlas-column transform (depth pass)
        readonly float[] _cascadeNormalOffsets = new float[ShadowSettings.MaxCascades];
        // Per-cascade dissolve noise scale for the DEPTH pass (issue #391): the base scale floored so a noise cell
        // never shrinks below ShadowDissolveNoise.MinCellTexels shadow texels. Derived beside the normal offsets,
        // from the same fitted radius + resolution.
        readonly float[] _cascadeNoiseScales = new float[ShadowSettings.MaxCascades];
        int _cascadeCount;                    // active cascade count this frame
        readonly Matrix4x4[] _lastCascadeCpuVps = new Matrix4x4[ShadowSettings.MaxCascades];    // last rendered pass's per-cascade CPU fit
        int _lastShadowCascadeCount;          // last rendered pass's cascade count
        int _lastShadowResolution;            // allocated per-cascade shadow-map resolution at the last rendered pass
        List<ShadowCasterSpan> _lastShadowCasterRuns = new();          // last pass's drawn caster spans (see Scene3D.ShadowCasters.cs)
        List<ShadowCasterInstance> _lastShadowCasterModels = new();    // last pass's caster world matrices + dissolves
        List<ShadowCasterSpan> _shadowCasterRunsScratch = new();       // this-frame scratch (swapped in on commit)
        List<ShadowCasterInstance> _shadowCasterModelsScratch = new();
        // Per-frame animated-water-surface requests (Rendering gap #5). Cleared each Begin() like the decal queue;
        // opt-in - an empty queue means the water pass (Rendering.WaterRenderer) never runs this frame.
        readonly List<WaterPlane> _waterPlanes = new();
        // Translucent unlit overlay-mesh draws (collision proxies etc.): queued in submission order, flushed into the
        // model FB after the beams and before the post chain (depth-interleaved). Cleared each Begin().
        readonly List<(MeshHandle Mesh, Matrix4x4 World)> _overlayMeshDraws = new();
        // Alpha billboards are queued as per-billboard items (centre/size/colour), sorted back-to-front by view
        // depth each frame (overlapping alpha must composite far-to-near), then expanded to the vertex stream.
        // Additive billboards stay a flat vertex list: additive blend is order-independent, so they skip the sort.
        readonly List<BillboardItem> _billboardAlphaItems = new();
        readonly List<BillboardRenderer.BillboardVertex> _billboardAlpha = new();
        readonly List<BillboardRenderer.BillboardVertex> _billboardAdditive = new();
        // Reused per-frame sort buffers (centres + keys + resulting order) shared across the sorted alpha batches.
        // Grown geometrically by TransparencySort; never LINQ/comparer-allocated in the hot path.
        readonly List<Vector3> _sortCenters = new();
        float[] _sortKeys = Array.Empty<float>();
        int[] _sortOrder = Array.Empty<int>();
        // Textured depth-interleaved billboards: queued in submission order (NOT split by blend, so additive and
        // alpha quads stay correctly ordered against each other), coalesced into same-texture+blend runs at render.
        readonly List<TexturedBillboardItem> _texBillboardItems = new();
        // The textured billboards reordered back-to-front by view depth (built each frame from _texBillboardItems),
        // then coalesced into runs. Sorting here composites overlapping depth-interleaved quads far-to-near.
        readonly List<TexturedBillboardItem> _texBillboardSorted = new();
        readonly List<TexturedBillboardRun> _texBillboardRuns = new();
        readonly List<BillboardRenderer.BillboardVertex> _texBillboardVerts = new();
        // Modern particle sprites (ParticleRenderer): queued in submission order, rebuilt back-to-front each
        // frame into _particleSorted (ONE premultiplied stream, so alpha and additive sprites interleave
        // correctly), then drawn as a single instanced call after the water pass. Cleared each Begin().
        readonly List<ParticleSprite> _particleSprites = new();
        // Per-frame screen-space distortion sprites (cleared each Begin like the particle queue). Whether any are
        // queued drives the lazy allocation of the offset field and whether the post apply pass runs.
        readonly List<DistortionSprite> _distortionSprites = new();
        readonly List<ParticleSprite> _particleSorted = new();
        // Cached delegate mapping a TextureHandle list index to its GPU texture, handed to the particle renderer's
        // per-atlas run batching (flipbook sprites). Cached so the per-frame draw allocates no closure.
        Func<int, IGpuTexture?>? _particleTexResolver;
        // Glowing beams (lasers/thrusters/tethers): queued in submission order, flushed as one additive draw
        // into the model FB alongside the textured billboards (depth-interleaved, so geometry occludes them).
        readonly List<BeamItem> _beamItems = new();
        readonly List<BeamRenderer.BeamVertex> _beamVerts = new();
        // Motion trails (weapon swings, thruster streaks, tracers): queued samples flushed as one draw per blend
        // into the model FB alongside the beams (depth-interleaved). _trailSamples is a flat pool the items index.
        readonly List<TrailItem> _trailItems = new();
        readonly List<TrailSample> _trailSamples = new();
        readonly List<TrailRenderer.TrailVertex> _trailVertsAdditive = new();
        readonly List<TrailRenderer.TrailVertex> _trailVertsAlpha = new();
        readonly List<Vector3> _trailScratchPos = new();
        readonly List<Vector2> _trailScratchUv = new();
        readonly List<float> _trailScratchAlpha = new();
        // Reused per-frame grouping buffers (cleared, not realloc) for GPU instancing.
        readonly List<ModelRenderer.InstanceData> _instanceData = new();
        readonly List<MeshRun> _runs = new();
        // Mesh-handle -> run-index scratch for GroupInstances, keyed by (Index, Generation) so two different
        // generations occupying the same freed-and-reused slot never merge into one run. Reused across frames
        // (Cleared, not reallocated) to keep the O(instances) grouping pass allocation-free.
        readonly Dictionary<(int Index, int Generation), int> _meshRunIndex = new();
        // Skinned mesh storage, parallel to the rigid mesh storage above.
        readonly List<SkinnedMeshEntry?> _skinnedMeshes = new();
        readonly MeshSlotMap _skinnedSlots = new();
        readonly SkinnedSceneInstances _skinnedInstances = new();
        // Per-frame composed bone palette for every skinned draw (cleared each Begin), and reused grouping buffers.
        // Per-frame bone palette, slot-packed: draw i's composed matrices live at [i*MaxBonesPerDraw ..], padded to
        // the per-draw window so each draw's dynamic-offset bind selects exactly its slice. Cleared each Begin().
        readonly List<Matrix4x4> _boneMatrices = new();
        // CPU skinning (the bone-buffer GPU read corrupts past element 0 in the windowed Veldrid/Metal swapchain
        // context, so skinned meshes are deformed on the CPU and drawn through the proven-clean no-bone model
        // pipeline). _skinnedCpuVerts caches each loaded mesh's source vertices (parallel to _skinnedMeshes); the
        // three reused lists are the per-frame deformed-vertex stream, the per-draw instance data, and the draw list.
        readonly List<SkinnedVertex[]?> _skinnedCpuVerts = new();
        readonly List<ModelVertex> _cpuSkinnedVerts = new();
        readonly List<ModelRenderer.InstanceData> _cpuSkinnedInstances = new();
        readonly List<CpuSkinnedDraw> _cpuSkinnedDraws = new();
        // GPU-skinning path (UseGpuSkinning): per-frame draw list. No CPU deform - each entry carries its rest-pose
        // buffers + the bone-palette slice + the per-draw matrices/material, packed into the combined UBO at draw time.
        readonly List<GpuSkinnedDraw> _gpuSkinnedDraws = new();
        Vector3 _billboardRight, _billboardUp;
        bool _billboardBasisValid;

        public IsoCamera3D Camera { get; } = new();

        /// <summary>
        /// Optional camera that overrides the built-in <see cref="Camera"/> for rendering this scene. Set it to a
        /// sibling camera (e.g. <see cref="FollowCamera3D"/>) to drive the view/projection from something other than
        /// the iso camera; null (the default) uses <see cref="Camera"/>. The override supplies only the read-only
        /// camera surface (<see cref="IIsoCamera3D"/>), so the caller owns its aspect ratio: set it from the
        /// framebuffer each frame. <see cref="Camera"/>'s aspect is still maintained by the scene.
        /// </summary>
        public IIsoCamera3D? CameraOverride { get; set; }

        /// <summary>The camera the render path reads this frame: <see cref="CameraOverride"/> if set, else <see cref="Camera"/>.</summary>
        IIsoCamera3D ActiveCamera => CameraOverride ?? Camera;

        public PixelPostProcessSettings Post { get; } = new();

        /// <summary>
        /// Camera-frustum culling of the main (visible) mesh pass: on by default. Each queued instance whose
        /// world-space bounding sphere lies entirely outside the camera frustum is skipped in the model + splat
        /// draws, so nothing the camera cannot see is rasterized. Culling is pixel-neutral by construction (it only
        /// removes provably-offscreen geometry); force it off to prove that (the off/on parity test) or to profile.
        /// The shadow depth pass is NEVER camera-culled: an off-screen caster still throws a visible shadow, so this
        /// flag does not touch the shadow pass (see <see cref="CulledInstances"/>).
        /// </summary>
        public bool FrustumCulling { get; set; } = true;

        /// <summary>
        /// Opt-in GPU skinning: when true, skinned draws are deformed on the GPU (the vertex shader blends the bone
        /// palette) instead of on the CPU. Default <b>OFF</b>. The design is the fold-matrix binding proven by
        /// <c>GpuSkinningReproGpuTests</c>: the skinned vertex reads ONE combined resource buffer at set 0
        /// (<c>{ Mvp; Model; params; bones[128] }</c>, per-draw dynamic offset) and a skinned <c>ModelFrag</c> variant
        /// reads frame + material data at set 1 (fragment only), sidestepping the Metal/Veldrid/SPIRV-Cross
        /// two-vertex-buffer mis-bind that pulled the old GPU path. The rest-pose vertex buffer uploads once at load.
        /// Only the per-draw palette + matrices upload each frame, so the CPU cost is a palette pack, not a full
        /// vertex deform - the win at MMO crowd scale. Rendering is pixel-parity with the CPU path (the shader mirrors
        /// <see cref="SkinningMath.SkinVertex"/>), and the shadow depth pass mirrors the flag. It ships OFF because the
        /// offscreen repro is necessary but not sufficient for the historical windowed swapchain context: flip it on
        /// for a windowed A/B against CPU skinning before relying on it (see docs/USING-KHAOZENGINE.md). Flippable per
        /// frame. A culled draw skips its palette upload just like the CPU path.
        /// </summary>
        public bool UseGpuSkinning { get; set; }

        // Per-instance visibility for the current frame's main pass, index-aligned to the grouped instance buffer
        // (_instanceData). Reused across frames (grown, never per-frame allocated). true = draw in the visible pass.
        bool[] _instanceVisible = Array.Empty<bool>();
        int _drawnInstances, _culledInstances;

        /// <summary>Number of mesh instances DRAWN in the main visible pass last frame (after frustum culling).
        /// Splat-terrain and model instances both count; skinned/overlay draws are separate. Cheap per-frame stat so
        /// a game can show the culling win. Zero until the first rendered frame.</summary>
        public int DrawnInstances => _drawnInstances;

        /// <summary>Number of mesh instances CULLED (skipped) in the main visible pass last frame by camera-frustum
        /// culling. Always 0 when <see cref="FrustumCulling"/> is off. Pairs with <see cref="DrawnInstances"/>
        /// (drawn + culled = the instances queued for a mesh with computable bounds this frame).</summary>
        public int CulledInstances => _culledInstances;

        // Per-frame skinned draw/cull counters, index-unaligned (skinned draws are not grouped into runs). Reset
        // each RenderInternal like _drawnInstances/_culledInstances above.
        int _drawnSkinnedInstances, _culledSkinnedInstances;

        /// <summary>Number of queued skinned draws actually CPU-skinned and drawn in the main visible pass last
        /// frame. Mirrors <see cref="DrawnInstances"/> for the skinned queue. A draw camera-culled from the main
        /// pass but still inside the active shadow map's ortho volume is still CPU-skinned and uploaded (so its
        /// shadow renders), but is not counted here - see <see cref="CulledSkinnedInstances"/>.</summary>
        public int DrawnSkinnedInstances => _drawnSkinnedInstances;

        /// <summary>Number of queued skinned draws CULLED from the main visible pass last frame by camera-frustum
        /// culling. Always 0 when <see cref="FrustumCulling"/> is off. Pairs with <see cref="DrawnSkinnedInstances"/>
        /// (drawn + culled = the valid skinned draws queued this frame). A camera-culled draw only skips its CPU
        /// skin + upload entirely when it is ALSO outside the active shadow map's ortho volume (or shadows are off) -
        /// the shadow depth pass is never camera-culled, matching <see cref="FrustumCulling"/>'s rigid-instance
        /// contract.</summary>
        public int CulledSkinnedInstances => _culledSkinnedInstances;

        /// <summary>
        /// Per-pass CPU encode timing: off by default (no cost, existing scenes byte-stable and no
        /// <c>Stopwatch</c> calls). Set <c>true</c> to populate <see cref="PassTimingsMs"/> each frame - a
        /// developer/profiling toggle, not a visual quality setting (compare <see cref="FrustumCulling"/>, which is
        /// pixel-neutral either way; this one changes nothing about rendered output in either state). See
        /// <see cref="Scene3DPassTimingsMs"/> remarks for exactly what is and is not measured.
        /// </summary>
        public bool EnableTiming { get; set; }

        Scene3DPassTimingsMs _passTimingsMs;

        /// <summary>Last-frame per-pass CPU encode milliseconds. All fields are 0 unless <see cref="EnableTiming"/>
        /// is (or was) true; stays at the last-recorded value once turned off (it is not reset to 0 by disabling).</summary>
        public Scene3DPassTimingsMs PassTimingsMs => _passTimingsMs;

        // Always-on per-frame draw counters (draw calls / instances / triangles / buffer-update bytes). Plain
        // increments in the submit path, reset each Begin(), read after RenderInternal via LastFrameStats. Unlike
        // EnableTiming (which brackets Stopwatch reads), these cost a handful of adds and stay on unconditionally.
        RenderFrameStats _frameStats;

        /// <summary>
        /// Last-frame GPU draw counters for this scene: draw-call, instance, and estimated-triangle totals over the
        /// geometry passes (rigid instanced, terrain splat, CPU-skinned, and the shadow-caster depth pass), one
        /// draw-call increment per effect/overlay submission (decals, water, sky, billboards, beams, trails,
        /// debug fills/lines), and the per-frame instance + skinned vertex upload bytes. Always on (no enable flag).
        /// Reset and finalized each frame inside the render pass (like <see cref="DrawnInstances"/> /
        /// <see cref="PassTimingsMs"/>), so read it after the scene has rendered the frame. A <see cref="Begin"/>
        /// without a render leaves the previous frame's totals. Aggregate it with a 2D batch's <c>FrameStats</c> via
        /// <see cref="RenderFrameStats.op_Addition"/> for a whole-frame total. Post-process fullscreen blits are not
        /// itemized (their CPU encode time shows in <see cref="PassTimingsMs"/>'s post field instead).
        /// </summary>
        public RenderFrameStats LastFrameStats => _frameStats;

        /// <summary>Last-frame water diagnostics (ocean FFT stall cost + clipmap rebuild count). Issue #374: these
        /// used to be internal-only counters on the water renderer, reachable by KhaozEngine's own tests but not by
        /// a consuming game. Always populated regardless of <see cref="EnableTiming"/> (the underlying counters are
        /// plain fields/a Stopwatch read that already run unconditionally inside the water renderer, the same
        /// always-on shape as <see cref="LastFrameStats"/>). See <see cref="WaterFrameStats"/> for exactly what each
        /// field means and when it goes stale.</summary>
        public WaterFrameStats LastWaterStats => new(_water.LastOceanCost.Stalls, _water.LastOceanCost.StallMs, _water.LastClipmapRebuilds);

        /// <summary>
        /// True when the last rendered frame SKIPPED the key-light shadow depth pass and reused the previous frame's
        /// shadow map. The pass is skipped only when every shadow-relevant input is unchanged since the last rendered
        /// pass: the fitted cascade matrices (<see cref="ComputeShadowCascades"/>, which fold in the light direction,
        /// focus, and camera), the rigid caster set + world transforms, the map resolution, and no animated skinned
        /// caster is present (a skinned caster's bone pose can change every frame, so it always re-renders).
        /// Always <c>false</c> when the resolved shadow tier is not <see cref="ShadowMode.ShadowMap"/>, and on any
        /// frame the depth pass re-rendered. A diagnostics/HUD signal for the static-scene shadow optimisation:
        /// presentation-neutral (a skipped frame shadows identically to a re-rendered one, since the map content is
        /// the same casters under the same matrix). A skipped pass contributes zero shadow draw calls to
        /// <see cref="LastFrameStats"/>.
        /// </summary>
        public bool ShadowPassSkippedLastFrame => _shadowPassSkippedLastFrame;

        /// <summary>Last-frame shadow depth-pass decision: each dirty reason on its own bit, plus what the pass
        /// recorded (per-cascade rigid spans, raw draw calls). See <see cref="ShadowPassDiagnostics"/>.</summary>
        public ShadowPassDiagnostics LastShadowPassDiagnostics => _lastShadowPassDiagnostics;

        /// <summary>
        /// GPU resources retired by a mid-life unload (a streamed chunk mesh freed while the scene keeps running)
        /// that the GPU has not provably finished with yet, so they are still waiting to be destroyed. Always on
        /// and allocation-free.
        /// <para>A streaming world's healthy shape is a small number that returns to 0 within a frame or two of a
        /// burst of unloads. A number that climbs and stays up means the frame loop is retiring faster than the GPU
        /// is retiring frames, or that a host driving the scene without frame boundaries (a tool, a test, an
        /// offscreen render) is never reaching the <c>BeginFrame</c> that frees them, in which case the whole
        /// unloaded ring is being held. Diagnostics only: reading it changes nothing.</para>
        /// </summary>
        public int RetiredResourceCount => _retired.PendingCount;

        /// <summary>Test/profiling seam onto the batched ground-decal renderer (its <c>ForceFullscreenQuads</c> parity
        /// toggle). Internal - the renderer type is internal. A GPU test flips the toggle to prove the footprint
        /// bounding renders pixel-identically to full-viewport coverage.</summary>
        internal Rendering.GroundDecalRenderer DecalRenderer => _decalRenderer;

        /// <summary>Host-set per-frame clock (seconds) driving beam pulse/scroll (see <see cref="DrawBeam"/> /
        /// <see cref="BeamStyle"/>). Set it once per frame in your draw callback (it runs after <see cref="Begin"/>),
        /// e.g. <c>scene.EffectTimeSeconds = totalSeconds</c>. NOT cleared by <see cref="Begin"/> - the host owns it.
        /// Presentation only; zero (never set) renders a static beam. A generic clock so future time-driven 3D
        /// effects can share it.</summary>
        public float EffectTimeSeconds { get; set; }

        /// <summary>Quality tier for the ground-decal pass (<see cref="GroundDecalQuality.Full"/> by default).
        /// <see cref="GroundDecalQuality.Reduced"/> drops the second noise octave and the edge sparkle for weak GPUs.
        /// The base fill, feathered edge, rim, and sweep energy are unchanged. Presentation only, host-owned
        /// (NOT cleared by <see cref="Begin"/>), so set it once when picking a graphics tier.</summary>
        public GroundDecalQuality DecalQuality { get; set; } = GroundDecalQuality.Full;

        /// <summary>Quality tier for the modern particle pass (<see cref="Render3D.ParticleQuality.Full"/> by
        /// default). <see cref="Render3D.ParticleQuality.Reduced"/> drops the second noise octave and the ember
        /// flicker for weak GPUs. Presentation-only and host-owned: NOT cleared by <see cref="Begin"/>.</summary>
        public ParticleQuality ParticleQuality { get; set; } = ParticleQuality.Full;

        /// <summary>Quality tier for the screen-space distortion pass (<see cref="Render3D.DistortionQuality.Full"/> by
        /// default). <see cref="Render3D.DistortionQuality.Reduced"/> drops the second heat noise octave and renders
        /// the offset field at quarter resolution instead of half. Presentation-only and host-owned: NOT cleared by
        /// <see cref="Begin"/>.</summary>
        public DistortionQuality DistortionQuality { get; set; } = DistortionQuality.Full;

        /// <summary>Soft-particle fade distance in world units for the modern particle pass: a sprite's coverage
        /// fades to zero as the scene surface behind it comes within this distance, so effects sit IN the world
        /// instead of clipping hard against geometry. 0 disables the fade (and its depth-texture work).
        /// Presentation-only and host-owned: NOT cleared by <see cref="Begin"/>.</summary>
        public float ParticleSoftFade { get; set; } = 0.35f;

        /// <summary>
        /// The active screen-space teleport transition (<see cref="HardBlink"/> / <see cref="CameraDissolve"/>, or a
        /// custom <see cref="IScreenTransition"/>), drawn as a fullscreen pass over the final image each frame. The
        /// consumer owns its lifecycle (Begin/Update on a teleport, gating the streaming hold); the scene captures the
        /// frozen frame for a crossfade and renders the overlay. Null (the default) renders nothing extra, so a scene
        /// with no transition is byte-identical. World-space effects (<see cref="CharDissolve"/>) are applied per-draw
        /// via <c>DrawSkinned</c> instead, not here. NOT cleared by <see cref="Begin"/> - the host owns it.
        /// </summary>
        public IScreenTransition? ScreenTransition { get; set; }

        /// <summary>Clears the active <see cref="ScreenTransition"/> and drops the renderer's captured frozen-frame
        /// state. For a consumer teardown that tears down mid-transition (a disconnect, a scene/screen swap): without
        /// this a transition left assigned (and never driven to <see cref="TransitionPhase.Done"/>) would hold the
        /// overlay covering the view forever. Does NOT drive the transition's own state machine (call
        /// <c>Transition.Reset</c> on the effect too if the consumer keeps reusing the instance); this just detaches it
        /// from the scene. Idempotent.</summary>
        public void ClearScreenTransition()
        {
            ScreenTransition = null;
            _transitions.Reset();
        }

        /// <summary>Maximum dynamic point lights consumed in one frame. <see cref="AddLight"/> accepts any number,
        /// but only the first <see cref="MaxPointLights"/> queued are uploaded (extras are dropped); the host is
        /// expected to pick the N nearest per frame so a dense bullet-hell stays within budget.</summary>
        public const int MaxPointLights = ModelRenderer.MaxPointLights;

        internal Scene3D(IGpuDevice gd, GpuOutputDescription targetOutput, ShadowSettings? initialShadows = null)
        {
            _gd = gd;
            // Bound once, here, to a method group: the delegate object is allocated exactly once for this Scene3D's
            // lifetime rather than once per frame (see the field doc comment above and AlphaCutoffFor below).
            _alphaCutoffLookup = AlphaCutoffFor;
            // Fence-polled ripeness where the backend can signal on GPU completion (Metal, Vulkan), the frame-count
            // drain everywhere else. TryCreate returning null IS the fallback, not a failure.
            _retired = new RetiredResourcePool(gd.WaitForIdle, GpuRetireBarrier.TryCreate(gd));
            _targetOutput = targetOutput;
            // Construction seam (issue #27): the shadow atlas is sized ONCE here (resolution x cascade count), and its
            // handle is bound into every material set, so those knobs can only be honoured if supplied BEFORE this
            // point. A caller seeds them by passing an initialShadows through the
            // Render3DSurface/Render3DPreview/Render3DSnapshot ctor. After the atlas is built we commit the settings, so
            // a later write to a construction-time knob throws instead of silently no-opping (the old inert behaviour).
            if (initialShadows != null) Post.Quality.Shadows = initialShadows;
            _res = new RenderResources(gd, Post.RenderWidth, Post.RenderHeight, Post.Hdr.Enabled);
            ShadowSettings shadow0 = Post.Quality.Shadows;
            _model = new ModelRenderer(gd, _res.ModelFB.Outputs,
                shadow0.ShadowMapResolution, shadow0.ResolvedCascadeCount);
            shadow0.CommitAtlas();
            _post = new PixelPostProcess(gd, _res.PingAFB.Outputs, targetOutput);
            _post.BindTargets(_res);
            _lines = new LineRenderer(gd, targetOutput);
            _fills = new FillRenderer(gd, targetOutput);
            _billboards = new BillboardRenderer(gd, targetOutput);
            _transitions = new TransitionRenderer(gd, targetOutput);
            _transitions.BindTargets(_res);
            // Textured billboards draw INTO the model MRT (depth-interleaved with meshes), so they target the model
            // framebuffer's output description, not the final target like the overlay renderers above.
            _texBillboards = new TexturedBillboardRenderer(gd, _res.ModelFB.Outputs);
            // Beams draw into the same model MRT as the textured billboards (depth-interleaved), so they target the
            // model framebuffer's output description.
            _beams = new BeamRenderer(gd, _res.ModelFB.Outputs);
            // Trails draw into the same model MRT as the beams (depth-interleaved), so they target the model
            // framebuffer's output description too.
            _trails = new TrailRenderer(gd, _res.ModelFB.Outputs);
            // Ground decals render into the lit color attachment + read-only scene depth (ColorDepthFB) before the
            // post chain, so they pass that framebuffer's output description (color format + depth format).
            _decalRenderer = new Rendering.GroundDecalRenderer(gd, _res.ColorDepthFB.Outputs);
            // Modern particle sprites render into the same ColorDepthFB (lit colour + read-only scene depth)
            // after the water pass, sampling the resolved scene depth for the soft fade. Default empty (no
            // DrawParticle call queued == the pass never runs, existing scenes byte-stable).
            _particleRenderer = new Rendering.ParticleRenderer(gd, _res.ColorDepthFB.Outputs);
            // Screen-space distortion writes into its own lazily allocated half/quarter-res offset field (a fixed
            // R16G16Float output, never multisampled, so it needs no SetOutputs), re-sampled by the post apply pass.
            // Default empty (no DrawDistortion call queued == nothing allocated, no apply pass, existing scenes byte-stable).
            _distortionRenderer = new Rendering.DistortionRenderer(gd);
            // The procedural sky renders into the same ColorDepthFB (lit colour + read-only scene depth) as the
            // decals, as a far-plane background pass behind the geometry. Default off (Post.Sky.Enabled == false).
            _sky = new Rendering.SkyRenderer(gd, _res.ColorDepthFB.Outputs);
            // Procedural starfield: a background pass at the same slot as the sky (before the decals), so the stars
            // are part of the scene and anything drawn over the void composites over them. Default on
            // (Post.Background == BackgroundMode.Starfield).
            _starfield = new Rendering.StarfieldRenderer(gd, _res.ColorDepthFB.Outputs);
            // Animated water draws into the same ColorDepthFB, AFTER the sky + decals (see RenderInternal). Default
            // off (no DrawWater request queued == the pass never runs, existing scenes byte-stable).
            _water = new Rendering.WaterRenderer(gd, _res.ColorDepthFB.Outputs);
            // Depth-tested debug wire volumes draw into the same ColorDepthFB (lit colour + read-only scene depth)
            // after the water pass, before the post chain, so scene geometry occludes their buried parts.
            _depthLines = new Rendering.DepthLineRenderer(gd, _res.ColorDepthFB.Outputs);
            _overlayMeshes = new Rendering.OverlayMeshRenderer(gd, _res.ModelFB.Outputs);
        }

        /// <summary>An opaque handle to an albedo texture loaded with <see cref="LoadTexture(string,TextureMipPolicy)"/> /
        /// <see cref="LoadTexture(byte[],int,int,TextureMipPolicy)"/>. Pass it to <see cref="LoadMesh(GltfMesh,TextureHandle)"/> to
        /// texture a mesh. Wraps an index into Scene3D's internal texture list; the GPU texture stays internal.</summary>
        public readonly struct TextureHandle
        {
            /// <summary>Index into the owning scene's texture list (0-based). Internal detail; do not interpret.</summary>
            internal readonly int Index;
            internal TextureHandle(int index) { Index = index + 1; } // store +1 so default == Invalid (Index 0)
            /// <summary>An invalid handle (the same as <c>default</c>). Loading a mesh with this is untextured.</summary>
            public static TextureHandle Invalid => default;
            /// <summary>True when this handle refers to a loaded texture (not the <c>default</c>/Invalid handle).</summary>
            public bool IsValid => Index != 0;
            /// <summary>The 0-based list index this handle refers to. Only meaningful when <see cref="IsValid"/>.</summary>
            internal int ListIndex => Index - 1;
        }

        /// <summary>An opaque handle to a splat-terrain material (5 tileable layers + triplanar params) loaded with
        /// <see cref="LoadSplatMaterial"/>. Pass it to <see cref="LoadMesh(GltfMesh,SplatMaterialHandle)"/> to draw a
        /// mesh through the splat pipeline. Shared across many meshes (e.g. every terrain chunk).</summary>
        public readonly struct SplatMaterialHandle
        {
            internal readonly int Index;
            internal SplatMaterialHandle(int index) { Index = index + 1; } // store +1 so default == Invalid
            public static SplatMaterialHandle Invalid => default;
            public bool IsValid => Index != 0;
            internal int ListIndex => Index - 1;
        }

        /// <summary>A bundle of optional surface maps for <see cref="LoadMesh(GltfMesh,SurfaceMaps)"/> and
        /// <see cref="LoadSkinnedMesh(SkinnedGltfMesh,SurfaceMaps)"/>:
        /// albedo, tangent-space normal, and roughness (glTF metallic-roughness .g convention). Any invalid
        /// (<c>default</c>) handle falls back to the renderer's default for that slot (white albedo, flat
        /// normal, zero roughness), so binding only some maps is fine. Load each map with
        /// <see cref="LoadTexture(string,TextureMipPolicy)"/> / <see cref="LoadTexture(byte[],int,int,TextureMipPolicy)"/>. <see cref="AlphaCutoff"/>
        /// carries the material's alpha-cutout threshold (0 = OPAQUE, no clip, see
        /// <see cref="GltfMaterialMaps.AlphaCutoff"/>).</summary>
        public readonly struct SurfaceMaps
        {
            public readonly TextureHandle Albedo;
            public readonly TextureHandle Normal;
            public readonly TextureHandle Roughness;
            /// <summary>Alpha-cutout threshold: 0 = OPAQUE (no clip), else the MASK cutoff. The model fragment
            /// discards a texel whose albedo alpha is below this, so a foliage/leaf-card texture renders as its
            /// silhouette instead of a solid quad. OPAQUE (0) is byte-identical to the pre-cutout render.</summary>
            public readonly float AlphaCutoff;
            public SurfaceMaps(TextureHandle albedo, TextureHandle normal = default, TextureHandle roughness = default,
                float alphaCutoff = 0f)
            {
                Albedo = albedo; Normal = normal; Roughness = roughness; AlphaCutoff = alphaCutoff;
            }
        }

        /// <summary>Upload a loaded mesh to the GPU once; returns a handle to instance it with <see cref="Draw(KhaozEngine.Render3D.MeshHandle, System.Numerics.Matrix4x4)"/>.
        /// Reuses a slot freed by <see cref="UnloadMesh"/> when one is available. The mesh is untextured (samples the
        /// renderer's 1x1 white default, so its colour is the baked vertex colour times any per-instance tint).</summary>
        public MeshHandle LoadMesh(GltfMesh mesh) => LoadMeshInternal(mesh, null);

        /// <summary>Upload a loaded mesh to the GPU once and bind <paramref name="texture"/> as its albedo. The
        /// fragment shader multiplies the sampled texel into the lit albedo (<c>texRgb * vColor * vTint</c>). An
        /// invalid/<c>default</c> <paramref name="texture"/> handle falls back to untextured (no throw).</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, TextureHandle texture)
        {
            IGpuResourceSet? material = null;
            if (texture.IsValid)
                material = _model.CreateMaterialSet(_textures[texture.ListIndex]!);
            return LoadMeshInternal(mesh, material);
        }

        /// <summary>Upload a mesh and bind a full PBR-lite material (<paramref name="maps"/>): albedo + optional
        /// normal + optional roughness. Invalid handles fall back to the renderer defaults. Normal perturbation
        /// requires the mesh to carry tangents (glTF meshes via <see cref="GltfLoader"/>, or
        /// MeshAssembler output); primitives have none and are lit by their geometric normal.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, SurfaceMaps maps)
        {
            IGpuTexture? a = maps.Albedo.IsValid ? _textures[maps.Albedo.ListIndex] : null;
            IGpuTexture? n = maps.Normal.IsValid ? _textures[maps.Normal.ListIndex] : null;
            IGpuTexture? r = maps.Roughness.IsValid ? _textures[maps.Roughness.ListIndex] : null;
            IGpuResourceSet? material = (a != null || n != null || r != null)
                ? _model.CreateMaterialSet(a, n, r)
                : null;
            return LoadMeshInternal(mesh, material, alphaCutoff: maps.AlphaCutoff);
        }

        /// <summary>Upload a mesh and draw it through the splat-terrain pipeline with <paramref name="material"/>
        /// (its vertex <c>Color</c> carries the packed splat weights). An invalid handle falls back to the untextured
        /// model path. The splat material is shared (owned by the scene); unloading the mesh does NOT free it.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, SplatMaterialHandle material)
        {
            if (!material.IsValid) return LoadMesh(mesh);
            return LoadMeshInternal(mesh, null, material.ListIndex);
        }

        MeshHandle LoadMeshInternal(GltfMesh mesh, IGpuResourceSet? material, int splatMaterial = -1, float alphaCutoff = 0f)
        {
            var f = _gd.Factory;
            var vb = f.CreateBuffer(new GpuBufferDescription((uint)(mesh.Vertices.Length * ModelVertex.SizeInBytes), GpuBufferUsage.VertexBuffer));
            _gd.UpdateBuffer(vb, 0, mesh.Vertices);
            var ib = CreateIndexBuffer(mesh.Indices32, mesh.IndexFormat);

            MeshBounds bounds = MeshBounds.FromVertices(mesh.Vertices);
            int index = _slots.Alloc(out int generation);
            var slot = new Mesh(vb, ib, mesh.Indices32.Length, mesh.IndexFormat, in bounds, material, splatMaterial, alphaCutoff);
            if (index < _meshes.Count) _meshes[index] = slot;   // reused freed slot
            else _meshes.Add(slot);                              // fresh appended slot
            return new MeshHandle(index, generation);
        }

        /// <summary>Create + fill a GPU index buffer matching the mesh's chosen <see cref="GpuIndexFormat"/>. A
        /// 16-bit mesh uploads a narrowed <see cref="ushort"/> buffer (byte-identical to the pre-32-bit path, so
        /// existing renders are unchanged); a 32-bit mesh uploads the full <see cref="uint"/> indices.</summary>
        IGpuBuffer CreateIndexBuffer(uint[] indices32, GpuIndexFormat format)
        {
            var f = _gd.Factory;
            if (format == GpuIndexFormat.UInt32)
            {
                var ib = f.CreateBuffer(new GpuBufferDescription((uint)(indices32.Length * sizeof(uint)), GpuBufferUsage.IndexBuffer));
                _gd.UpdateBuffer(ib, 0, indices32);
                return ib;
            }
            var i16 = new ushort[indices32.Length];
            for (int i = 0; i < i16.Length; i++) i16[i] = (ushort)indices32[i];
            var ib16 = f.CreateBuffer(new GpuBufferDescription((uint)(i16.Length * sizeof(ushort)), GpuBufferUsage.IndexBuffer));
            _gd.UpdateBuffer(ib16, 0, i16);
            return ib16;
        }

        /// <summary>Decode a PNG/JPG file into an albedo texture (RGBA8) and return a handle for
        /// <see cref="LoadMesh(GltfMesh,TextureHandle)"/>. The texture is owned by the scene and freed in
        /// <see cref="Dispose"/>; it may be shared across several meshes.</summary>
        /// <param name="pngPath">Path to the image file.</param>
        /// <param name="policy">How much of the mip chain to build. The default is
        /// <see cref="TextureMipPolicy.Full"/>.</param>
        public TextureHandle LoadTexture(string pngPath, TextureMipPolicy policy = default)
        {
            ImageRgba img = ImageRgba.Load(pngPath);
            return LoadTexture(img.Pixels, img.Width, img.Height, policy);
        }

        /// <summary>Create an albedo texture from raw RGBA8 bytes (row-major, <paramref name="width"/> *
        /// <paramref name="height"/> * 4 bytes) and return a handle. For procedural textures and tests. The texture
        /// is owned by the scene and freed in <see cref="Dispose"/>.
        /// <para>A MIPPED LOAD IS NOT A MID-FRAME CALL: generating the chain needs a command list of its own, so
        /// this refuses with <see cref="GpuNestedRecordingException"/> while a frame is recording (#424). A
        /// one-level load opens no list and is unaffected.</para></summary>
        /// <param name="rgba">Row-major RGBA8 pixels.</param>
        /// <param name="width">Texture width in texels.</param>
        /// <param name="height">Texture height in texels.</param>
        /// <param name="policy">How much of the mip chain to build. The default is
        /// <see cref="TextureMipPolicy.Full"/>, which is what every caller got before the policy existed. Pass
        /// <see cref="TextureMipPolicy.None"/> or <see cref="TextureMipPolicy.AtlasGrid"/> for an image whose
        /// regions must not average into each other.</param>
        public TextureHandle LoadTexture(byte[] rgba, int width, int height, TextureMipPolicy policy = default)
        {
            // Create, upload and mip in one place that also owns the failure path: the mid-frame refusal throws
            // after the texture exists and before the scene has taken it, so TextureUploads frees it rather than
            // leaking one per attempt (#424).
            _textures.Add(TextureUploads.CreateMipped(
                _gd, rgba, (uint)width, (uint)height, policy.LevelsFor(width, height), "Scene3D.LoadTexture"));
            return new TextureHandle(_textures.Count - 1);
        }

        /// <summary>Upload a 5-layer splat-terrain material: two texture arrays (albedo + tangent-space normal, one
        /// layer per <see cref="SplatLayerImage"/>, all the same <paramref name="width"/> x <paramref name="height"/>
        /// RGBA8), with full mip chains generated, plus a params UBO (per-layer tint/tiling/roughness + triplanar
        /// sharpness + projection + base specular). Returns a handle to draw meshes through the splat pipeline. The
        /// material is owned by the scene and freed in <see cref="Dispose"/> (or <see cref="UnloadSplatMaterial"/>);
        /// it is shared across every mesh that references it (e.g. all terrain chunks).
        /// <para>NOT A MID-FRAME CALL either: the two mip chains need a command list of their own, so this
        /// refuses with <see cref="GpuNestedRecordingException"/> while a frame is recording (#424).</para></summary>
        public SplatMaterialHandle LoadSplatMaterial(int width, int height, IReadOnlyList<SplatLayerImage> layers,
            float triplanarSharpness = 8f, SplatProjection projection = SplatProjection.Triplanar, float baseSpecStrength = 0.15f,
            TerrainSamplerConfig? sampler = null)
        {
            if (layers.Count != SplatMaterialConfig.LayerCount)
                throw new ArgumentException($"a splat material needs exactly {SplatMaterialConfig.LayerCount} layers, got {layers.Count}.", nameof(layers));
            uint w = (uint)width, h = (uint)height, mips = SplatMaterialConfig.MipLevelCount(width, height);
            // Both arrays, uploaded and mipped in one transient list, and freed TOGETHER if that list is refused
            // mid-frame: two 5-layer mipped arrays are the most expensive thing a refusal could have stranded
            // (#424).
            (IGpuTexture albedo, IGpuTexture normal) =
                TextureUploads.CreateSplatArrays(_gd, w, h, mips, layers, "Scene3D.LoadSplatMaterial");

            var data = SplatMaterialConfig.BuildParams(layers, triplanarSharpness, projection, baseSpecStrength);
            // Combined UBO: frame uniforms (re-synced each frame in the splat pass) + these params appended. One
            // uniform buffer for the whole splat pipeline (Metal mis-binds a second UBO; see ModelRenderer).
            var ubo = _model.CreateSplatParamsUbo(in data);

            // A material that overrides the sampler gets its own (owned, disposed with the material); otherwise the
            // set binds the renderer's shared default sampler and nothing extra is owned here.
            IGpuSampler? ownedSampler = sampler.HasValue ? _model.CreateTerrainSampler(sampler.Value) : null;
            var set = ownedSampler is null
                ? _model.CreateSplatMaterialSet(ubo, albedo, normal)
                : _model.CreateSplatMaterialSet(ubo, albedo, normal, ownedSampler);
            _splatMaterials.Add(new SplatMaterialEntry(albedo, normal, ubo, set, ownedSampler));
            return new SplatMaterialHandle(_splatMaterials.Count - 1);
        }

        /// <summary>Upload a glTF material's auto-read <see cref="GltfMaterialMaps"/> (from
        /// <see cref="GltfLoader.LoadWithMaterial"/> / <see cref="GltfLoader.LoadSkinnedWithMaterial"/>) into a
        /// <see cref="SurfaceMaps"/>: one <see cref="LoadTexture(byte[],int,int,TextureMipPolicy)"/> per present map. An absent map
        /// stays a <c>default</c> handle (the renderer falls back to its default for that slot - white albedo, flat
        /// normal, zero roughness), so an all-absent <paramref name="maps"/> yields an all-default
        /// <see cref="SurfaceMaps"/>. The uploaded textures are owned by the scene and freed in
        /// <see cref="Dispose"/>. Pass the result to <see cref="LoadMesh(GltfMesh,SurfaceMaps)"/> /
        /// <see cref="LoadSkinnedMesh(SkinnedGltfMesh,SurfaceMaps)"/>.</summary>
        public SurfaceMaps LoadSurfaceMaps(GltfMaterialMaps maps)
        {
            TextureHandle Upload(DecodedImage? img) =>
                img is { } i ? LoadTexture(i.Rgba, i.Width, i.Height) : default;
            return new SurfaceMaps(Upload(maps.Albedo), Upload(maps.Normal), Upload(maps.Roughness), maps.AlphaCutoff);
        }

        /// <summary>Opt-in convenience: upload a mesh and bind a glTF material's auto-read
        /// <see cref="GltfMaterialMaps"/> in one call - equivalent to
        /// <c>LoadMesh(mesh, LoadSurfaceMaps(maps))</c>. Absent maps fall back to the renderer defaults; an
        /// all-absent <paramref name="maps"/> loads the mesh untextured.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, GltfMaterialMaps maps) => LoadMesh(mesh, LoadSurfaceMaps(maps));

        /// <summary>Opt-in convenience: upload a skinned mesh and bind a glTF material's auto-read
        /// <see cref="GltfMaterialMaps"/> in one call - equivalent to
        /// <c>LoadSkinnedMesh(mesh, LoadSurfaceMaps(maps))</c>.</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, GltfMaterialMaps maps) =>
            LoadSkinnedMesh(mesh, LoadSurfaceMaps(maps));

        /// <summary>An opaque handle to a multi-material prop loaded with <see cref="LoadProp"/>: one textured
        /// sub-mesh per source material (bark + leaves, post + sign, ...), each with its own texture binding.
        /// <see cref="Draw(PropHandle,Matrix4x4,Color)"/> queues every part at one world transform (so the whole
        /// prop instances as a unit); <see cref="UnloadProp"/> frees them all. Wraps the per-part
        /// <see cref="MeshHandle"/>s; the array is owned by the handle.</summary>
        public readonly struct PropHandle
        {
            internal readonly MeshHandle[] Parts;
            internal PropHandle(MeshHandle[] parts) { Parts = parts; }
            /// <summary>True when this handle refers to a loaded prop (not the <c>default</c>/empty handle).</summary>
            public bool IsValid => Parts is { Length: > 0 };
            /// <summary>The number of textured sub-meshes (material parts) this prop is drawn as.</summary>
            public int PartCount => Parts?.Length ?? 0;
        }

        /// <summary>Upload a multi-material prop (one textured sub-mesh per source material, from
        /// <see cref="PropLoader.LoadPropParts"/> or <see cref="GltfLoader.LoadPartsWithMaterials"/>) and return a
        /// <see cref="PropHandle"/> that draws them as one unit. Each part is a normal textured mesh
        /// (<see cref="LoadMesh(GltfMesh,GltfMaterialMaps)"/>), so it flows through the same instanced draw path -
        /// this is the multi-texture-per-primitive render path: distinct textures on distinct sub-ranges, instanced
        /// correctly. Throws <see cref="ArgumentException"/> if <paramref name="parts"/> is empty.</summary>
        public PropHandle LoadProp(IReadOnlyList<GltfMeshPart> parts) => new PropHandle(LoadPartHandles(parts));

        /// <summary>Upload a multi-material prop's parts (from <see cref="PropLoader.LoadPropParts"/> /
        /// <see cref="PropLoader.LoadPropAuto"/> / <see cref="GltfLoader.LoadPartsWithMaterials"/>) and return the raw
        /// per-part <see cref="MeshHandle"/> list, one textured sub-mesh per source material. This is the multi-part
        /// scatter form: drop the list into a <c>id -&gt; parts</c> map for
        /// <c>KhaozEngine.Terrain.PropRenderer</c>'s multi-part draw path, where each part instances at the shared
        /// placement transform. A single-part list (a flat prop) yields one handle, drawn byte-identically to a plain
        /// <see cref="LoadMesh(GltfMesh,GltfMaterialMaps)"/>. Prefer <see cref="LoadProp(IReadOnlyList{GltfMeshPart})"/>
        /// when you want the parts bundled as one <see cref="PropHandle"/> (owned/unloaded as a unit). Prefer this
        /// when the caller owns the handles (e.g. shared across streamed chunks). Throws
        /// <see cref="ArgumentException"/> if <paramref name="parts"/> is empty.</summary>
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) => LoadPartHandles(parts);

        MeshHandle[] LoadPartHandles(IReadOnlyList<GltfMeshPart> parts)
        {
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            if (parts.Count == 0) throw new ArgumentException("a prop needs at least one material part.", nameof(parts));
            var handles = new MeshHandle[parts.Count];
            for (int i = 0; i < parts.Count; i++) handles[i] = LoadMesh(parts[i].Mesh, parts[i].Maps);
            return handles;
        }

        /// <summary>Queue every part of <paramref name="prop"/> at <paramref name="world"/> with a white tint. Each
        /// part is a separate instanced mesh sharing the transform, so the whole prop moves as one and multiple
        /// draws of the same prop batch as instances. A <c>default</c>/invalid handle is a no-op.</summary>
        public void Draw(PropHandle prop, Matrix4x4 world) => Draw(prop, world, Color.White);

        /// <summary>Queue every part of <paramref name="prop"/> at <paramref name="world"/> tinted by
        /// <paramref name="tint"/> (multiplied into each part's albedo). A <c>default</c>/invalid handle is a
        /// no-op.</summary>
        public void Draw(PropHandle prop, Matrix4x4 world, Color tint)
        {
            if (prop.Parts == null) return;
            foreach (MeshHandle part in prop.Parts) _instances.Add(part, world, tint);
        }

        /// <summary>Free every sub-mesh of <paramref name="prop"/> (each via <see cref="UnloadMesh"/>) and its
        /// textures' owning scope. A <c>default</c>/invalid handle is a no-op.</summary>
        public void UnloadProp(PropHandle prop)
        {
            if (prop.Parts == null) return;
            foreach (MeshHandle part in prop.Parts) UnloadMesh(part);
        }

        /// <summary>
        /// Free the GPU buffers backing <paramref name="h"/> and release its slot for reuse. A <c>default</c>
        /// handle is a no-op. A stale or bogus handle (its generation no longer matches the slot, e.g. a
        /// double-free) throws <see cref="ArgumentException"/>.
        /// </summary>
        public void UnloadMesh(MeshHandle h)
        {
            if (h.Generation == 0) return;          // default handle: no-op
            _slots.Free(h.Index, h.Generation);     // throws on stale/invalid
            // Retire rather than destroy: queued GPU work may still reference these buffers, and draining the whole
            // device per unload stalled the frame thread on the terrain streaming path (every chunk leaving the ring
            // and every LOD flip lands here). The pool frees them behind one drain a few frames later. The per-mesh
            // material set goes too, but NOT the texture: that is owned in _textures and shared between meshes.
            if (_meshes[h.Index] is { } mesh) _retired.Retire(mesh.Vb, mesh.Ib, mesh.MaterialSet);
            _meshes[h.Index] = null;
        }

        /// <summary>Free a splat-terrain material's GPU resources (its texture arrays, params UBO, resource set) and
        /// release its slot. A <c>default</c>/Invalid handle is a no-op. Meshes still referencing it must be unloaded
        /// first (they hold no reference after this). Also a no-op once <see cref="Dispose"/> has run: Dispose
        /// already freed every splat material and cleared the backing list, so a caller that still holds a handle
        /// (e.g. a world disposed after its owning scene) would otherwise index past the end of the now-empty list
        /// and get an <see cref="ArgumentOutOfRangeException"/> instead of a silent no-op.</summary>
        public void UnloadSplatMaterial(SplatMaterialHandle h)
        {
            if (!h.IsValid || h.ListIndex >= _splatMaterials.Count) return;
            var m = _splatMaterials[h.ListIndex];
            // Queued GPU work may still reference the material's arrays/UBO/set, so drain the device first.
            if (m != null) { _gd.WaitForIdle(); m.Dispose(); }
            _splatMaterials[h.ListIndex] = null;
        }

        /// <summary>Diagnostic: read one mip level (and array layer) of a splat material's ALBEDO texture array back
        /// to the CPU as packed RGBA8; <paramref name="width"/>/<paramref name="height"/> receive that mip's own
        /// dimensions. Lets a game/test verify the generated mip chain on a real device - e.g. whether a high mip is
        /// a real blurred downsample (its average colour matches mip 0, low detail) versus a copy of mip 0 (still
        /// detailed) or empty (near-black), which is how a broken GPU mip generation shows up. Requires a mappable
        /// device; not on the per-frame path.</summary>
        public byte[] DebugReadSplatAlbedoMip(SplatMaterialHandle h, int mipLevel, int arrayLayer, out int width, out int height)
        {
            if (!h.IsValid) throw new ArgumentException("splat material handle is Invalid.", nameof(h));
            var m = _splatMaterials[h.ListIndex] ?? throw new ArgumentException("splat material is not loaded (already unloaded).", nameof(h));
            var tex = m.AlbedoArray;
            if (mipLevel < 0 || (uint)mipLevel >= tex.MipLevels)
                throw new ArgumentOutOfRangeException(nameof(mipLevel), $"mip {mipLevel} out of range (texture has {tex.MipLevels} levels).");
            if (arrayLayer < 0)
                throw new ArgumentOutOfRangeException(nameof(arrayLayer));
            width = Math.Max(1, (int)tex.Width >> mipLevel);
            height = Math.Max(1, (int)tex.Height >> mipLevel);
            return GpuReadback.ToRgbaMip(_gd, tex, (uint)mipLevel, (uint)arrayLayer, width, height);
        }

        /// <summary>Diagnostic: read the key-light shadow depth map (R32F light-space depth) back to the CPU as a
        /// float array, row-major, top-left. Lets a test/tool verify the depth pass on a real device (e.g. that
        /// casters wrote near depths and the cleared background stayed 1.0). Requires a mappable device; not on the
        /// per-frame path. Mirrors <see cref="DebugReadSplatAlbedoMip"/>.</summary>
        internal float[] DebugReadShadowMap(out int width, out int height)
        {
            var tex = _model.ShadowMap.ShadowTexture;
            width = (int)tex.Width; height = (int)tex.Height;
            var f = _gd.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                tex.Width, tex.Height, GpuPixelFormat.R32Float, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                using (GpuRecording.Open(_gd, cl, "Scene3D.DebugReadShadowMap")) cl.CopyTexture(tex, staging);
                _gd.Submit(cl); _gd.WaitForIdle();
            }
            var outF = new float[width * height];
            var map = _gd.Map(staging, GpuMapMode.Read);
            unsafe
            {
                byte* data = (byte*)map.Data;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        outF[y * width + x] = *(float*)(data + y * (int)map.RowPitch + x * 4);
            }
            _gd.Unmap(staging);
            return outF;
        }

        /// <summary>Free the GPU texture backing <paramref name="h"/> (and its lazily-created textured-billboard
        /// resource set) and null its slot. A <c>default</c>/Invalid handle is a no-op; unloading an
        /// already-unloaded slot is also a no-op. The slot is NOT recycled, so handles stay stable. Because a
        /// texture can be shared by several meshes/materials, the scene can't know who else references it - any mesh
        /// still bound to this texture must be unloaded first or simply not drawn afterwards (mirrors
        /// <see cref="UnloadSplatMaterial"/>). Without this, textures only free at <see cref="Dispose"/>, so a
        /// long-lived scene that streams or reloads textured assets leaks one native texture per load. Also a no-op
        /// once <see cref="Dispose"/> has run: Dispose already freed every texture and cleared the backing list, so
        /// a caller that still holds a handle (e.g. a world disposed after its owning scene) would otherwise index
        /// past the end of the now-empty list and get an <see cref="ArgumentOutOfRangeException"/> instead of a
        /// silent no-op (mirrors <see cref="UnloadSplatMaterial"/>'s guard).</summary>
        public void UnloadTexture(TextureHandle h)
        {
            if (!h.IsValid || h.ListIndex >= _textures.Count) return;
            int i = h.ListIndex;
            // The 1-mip LoadTexture path returns with its UpdateTexture staging copy still queued on the
            // device (the mips>1 path already flushes via Submit+WaitForIdle). Destroying the texture while
            // that copy is in flight is a use-after-free the driver may survive silently (hardware) or crash
            // on (Mesa lavapipe's async queue thread segfaults executing the stale copy). Drain, then dispose.
            if (_textures[i] != null) _gd.WaitForIdle();
            _textures[i]?.Dispose();
            _textures[i] = null;
            if (i < _texBillboardSets.Count) { _texBillboardSets[i]?.Dispose(); _texBillboardSets[i] = null; }
            // The particle renderer caches per-atlas resource sets keyed by this list index, so drop them too. A
            // later load reusing this freed slot would otherwise bind the stale (disposed) texture.
            _particleRenderer.InvalidateTextureSets();
        }

        // Map a TextureHandle list index to its GPU texture for the particle renderer's flipbook run batching. Out
        // of range or unloaded slots return null, so the renderer falls back to its dummy textures.
        IGpuTexture? ResolveTextureByListIndex(int listIndex) =>
            listIndex >= 0 && listIndex < _textures.Count ? _textures[listIndex] : null;

        /// <summary>Number of texture slots still holding a live GPU texture (loaded and not yet unloaded). For tests.</summary>
        internal int LiveTextureCount
        {
            get { int n = 0; foreach (var t in _textures) if (t != null) n++; return n; }
        }

        /// <summary>Mip-level count of the GPU texture backing <paramref name="h"/> (0 if the slot is empty). For tests
        /// (guards the mip-chain invariant that keeps distant model/prop surfaces from aliasing).</summary>
        internal uint MipLevelsOf(TextureHandle h) => h.IsValid ? _textures[h.ListIndex]?.MipLevels ?? 0u : 0u;

        /// <summary>Number of mesh slots still holding a live GPU mesh (loaded and not yet unloaded). For tests
        /// (e.g. a streaming sink's teardown must return this to its pre-load baseline - no leaked chunk meshes).</summary>
        internal int LiveMeshCount
        {
            get { int n = 0; foreach (var m in _meshes) if (m != null) n++; return n; }
        }

        /// <summary>Number of splat-material slots still holding a live material (loaded and not yet unloaded). For tests.</summary>
        internal int LiveSplatMaterialCount
        {
            get { int n = 0; foreach (var m in _splatMaterials) if (m != null) n++; return n; }
        }

        /// <summary>Upload a skinned mesh to the GPU once; returns a handle to draw it with
        /// <see cref="DrawSkinned(KhaozEngine.Render3D.SkinnedMeshHandle, System.ReadOnlySpan{System.Numerics.Matrix4x4}, System.Numerics.Matrix4x4, KhaozEngine.Primitives.Color)"/>. Untextured (samples the 1x1 white default, so colour is the baked vertex
        /// colour times any per-instance tint).</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh) => LoadSkinnedInternal(mesh, null, null, null);

        /// <summary>Upload a skinned mesh and bind <paramref name="texture"/> as its albedo
        /// (<c>texRgb * vColor * vTint</c>). An invalid handle falls back to untextured.</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, TextureHandle texture)
        {
            IGpuTexture? a = texture.IsValid ? _textures[texture.ListIndex] : null;
            return LoadSkinnedInternal(mesh, a, null, null);
        }

        /// <summary>Upload a skinned mesh and bind a full PBR-lite material (<paramref name="maps"/>): albedo +
        /// optional normal + optional roughness, mirroring <see cref="LoadMesh(GltfMesh,SurfaceMaps)"/>. Invalid
        /// handles fall back to the renderer defaults (white albedo / flat normal / zero roughness). Normal
        /// perturbation requires the mesh to carry tangents - skinned glTF via <see cref="GltfLoader.LoadSkinned"/>
        /// or <see cref="SkinnedMeshBuilder"/> output both compute them; a tangent-less skinned vertex is lit by its
        /// geometric normal. The tangent rides the per-frame CPU skin deform so the TBN tracks the pose.</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, SurfaceMaps maps)
        {
            IGpuTexture? a = maps.Albedo.IsValid ? _textures[maps.Albedo.ListIndex] : null;
            IGpuTexture? n = maps.Normal.IsValid ? _textures[maps.Normal.ListIndex] : null;
            IGpuTexture? r = maps.Roughness.IsValid ? _textures[maps.Roughness.ListIndex] : null;
            return LoadSkinnedInternal(mesh, a, n, r);
        }

        // Builds BOTH the set-0 CPU-path material set and the set-1 GPU-skinning material set from the same textures,
        // so UseGpuSkinning can flip live (windowed A/B) without reloading meshes. Untextured (all null) leaves both
        // null and each path falls back to its renderer default set (white/flat/zero).
        SkinnedMeshHandle LoadSkinnedInternal(SkinnedGltfMesh mesh, IGpuTexture? albedo, IGpuTexture? normal, IGpuTexture? roughness)
        {
            var f = _gd.Factory;
            var vb = f.CreateBuffer(new GpuBufferDescription((uint)(mesh.Vertices.Length * SkinnedVertex.SizeInBytes), GpuBufferUsage.VertexBuffer));
            _gd.UpdateBuffer(vb, 0, mesh.Vertices);
            var ib = CreateIndexBuffer(mesh.Indices32, mesh.IndexFormat);

            bool textured = albedo != null || normal != null || roughness != null;
            IGpuResourceSet? material = textured ? _model.CreateMaterialSet(albedo, normal, roughness) : null;
            IGpuResourceSet? skinnedMaterial = textured ? _model.CreateSkinnedMaterialSet(albedo, normal, roughness) : null;

            MeshBounds bounds = MeshBounds.FromVertices(mesh.Vertices);
            int index = _skinnedSlots.Alloc(out int generation);
            var entry = new SkinnedMeshEntry(vb, ib, mesh.Indices32.Length, mesh.IndexFormat, material, skinnedMaterial, mesh.InverseBind, in bounds);
            // Cache the source vertices (parallel to _skinnedMeshes) for per-frame CPU skinning - no GPU readback.
            if (index < _skinnedMeshes.Count) { _skinnedMeshes[index] = entry; _skinnedCpuVerts[index] = mesh.Vertices; }
            else { _skinnedMeshes.Add(entry); _skinnedCpuVerts.Add(mesh.Vertices); }
            return new SkinnedMeshHandle(index, generation);
        }

        /// <summary>Queue one skinned draw. <paramref name="boneMatrices"/> are this frame's joint world
        /// transforms (model space), one per bone in the mesh's skin; the engine composes them with the mesh's
        /// inverse-bind. Passing the mesh's <see cref="SkinnedGltfMesh.RestPose"/> yields no deformation.
        /// Presentation only - never feed sim/RNG/netcode from bone state.</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint)
            => DrawSkinned(h, boneMatrices, model, tint, Material.None);

        /// <summary>As <see cref="DrawSkinned(SkinnedMeshHandle,ReadOnlySpan{Matrix4x4},Matrix4x4,Color)"/> with an
        /// explicit <paramref name="material"/> (emissive + specular).</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint, Material material)
        {
            if (!_skinnedSlots.IsValid(h.Index, h.Generation)) return;
            var entry = _skinnedMeshes[h.Index];
            if (entry is null) return;
            // This draw's bones go into slot N (N = its submission index), padded to the per-draw window so the
            // dynamic-offset bind selects exactly this draw's palette. Slot N maps to bone byte offset
            // N * SlotBytes and to instance buffer element N in the render loop.
            int slot = _skinnedInstances.Items.Count;
            ComposeBonesIntoSlot(_boneMatrices, slot, boneMatrices, entry.InverseBind);
            _skinnedInstances.Add(h, model, tint, material);
        }

        /// <summary>As the material overload, but dissolves the mesh for a <see cref="CharDissolve"/> teleport:
        /// <paramref name="dissolve"/> is the 0..1 threshold (0 = solid, 1 = fully gone; feed
        /// <see cref="ITransition.Cover"/>), with a glowing emissive edge of <paramref name="edgeColor"/> and width
        /// <paramref name="edgeWidth"/> (a fraction of the noise range). A <paramref name="dissolve"/> of 0 draws
        /// exactly like the material overload (the normal pipeline), so it is safe to call unconditionally while
        /// gating the value on the transition.</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint,
            Material material, float dissolve, float edgeWidth, Color edgeColor)
        {
            if (!_skinnedSlots.IsValid(h.Index, h.Generation)) return;
            var entry = _skinnedMeshes[h.Index];
            if (entry is null) return;
            int slot = _skinnedInstances.Items.Count;
            ComposeBonesIntoSlot(_boneMatrices, slot, boneMatrices, entry.InverseBind);
            _skinnedInstances.Add(h, model, tint, material, dissolve, edgeWidth, edgeColor);
        }

        /// <summary>Free a skinned mesh's GPU buffers and release its slot. A <c>default</c> handle is a no-op; a
        /// stale handle throws.</summary>
        public void UnloadSkinnedMesh(SkinnedMeshHandle h)
        {
            if (h.Generation == 0) return;
            _skinnedSlots.Free(h.Index, h.Generation);
            var m = _skinnedMeshes[h.Index];
            // Queued GPU work may still reference these resources, so drain the device before destroying them.
            if (m is { } e)
            {
                _gd.WaitForIdle();
                e.Vb.Dispose(); e.Ib.Dispose(); e.MaterialSet?.Dispose(); e.SkinnedMaterialSet?.Dispose();
            }
            _skinnedMeshes[h.Index] = null;
            _skinnedCpuVerts[h.Index] = null;
        }

        /// <summary>Skinned draws queued this frame. Internal: lets tests assert Begin clears the queue.</summary>
        internal int SkinnedInstanceCount => _skinnedInstances.Items.Count;

        /// <summary>Start a frame: latch <see cref="RenderOrigin"/>, then clear the instance queue, the point-light
        /// queue, the debug-line queue, the filled-overlay queue, and the billboard queues. Call before submitting.</summary>
        public void Begin()
        {
            _retired.BeginFrame();   // frees mid-life mesh buffers whose retirement fence has signaled (no stall)
            LatchRenderOrigin();
            _instances.Begin();
            _skinnedInstances.Begin();
            _boneMatrices.Clear();
            _lights.Clear();
            _lineVerts.Clear();
            _depthLineVerts.Clear();
            _fillVerts.Clear();
            _decals.Clear();
            _shadowBlobs.Clear();
            _waterPlanes.Clear();
            _overlayMeshDraws.Clear();
            _billboardAlphaItems.Clear();
            _billboardAlpha.Clear();
            _billboardAdditive.Clear();
            _particleSprites.Clear();
            _particleSorted.Clear();
            _distortionSprites.Clear();
            _texBillboardItems.Clear();
            _beamItems.Clear();
            _trailItems.Clear();
            _trailSamples.Clear();
            _billboardBasisValid = false; _framePrepared = false;   // per-frame latches (see PrepareFrame)
        }

        /// <summary>Queue one instance: draw <paramref name="mesh"/> at world transform <paramref name="world"/> (no tint).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world) => _instances.Add(mesh, world, Color.White);

        /// <summary>Queue one instance with a per-instance RGBA <paramref name="tint"/> that multiplies the lit color.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint) => _instances.Add(mesh, world, tint);

        /// <summary>Queue one instance with a per-instance <paramref name="tint"/> and <paramref name="material"/>
        /// (emissive glow + specular).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material) => _instances.Add(mesh, world, tint, material);

        /// <summary>As the material overload, but dissolves this rigid instance (issue #253): <paramref name="dissolve"/>
        /// is the 0..1 threshold (0 = solid, 1 = fully gone), with a glowing emissive edge of <paramref name="edgeColor"/>
        /// and width <paramref name="edgeWidth"/> (a fraction of the noise range). Mirrors the <see cref="DrawSkinned(SkinnedMeshHandle,ReadOnlySpan{Matrix4x4},Matrix4x4,Color,Material,float,float,Color)"/>
        /// dissolve overload but on the instanced path: no pipeline switch and no batching change (the discard folds
        /// into the shared ModelFrag), so it stays one instanced draw per mesh. A <paramref name="dissolve"/> of 0
        /// draws exactly like the material overload, so it is safe to call unconditionally while gating the value on a
        /// fade. Presentation only - never feed sim/RNG/netcode from the dissolve value.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolve, float edgeWidth, Color edgeColor)
            => _instances.Add(mesh, world, tint, material, dissolve, edgeWidth, edgeColor);

        // ---- Dynamic point/effect lights (muzzle flashes, explosions, thrusters, key projectiles). ----

        /// <summary>
        /// Queue a dynamic point light at <paramref name="worldPos"/> for this frame: it adds diffuse (and cheap
        /// specular) to the lit mesh pass, on top of the global key+fill+ambient term, falling smoothly to zero at
        /// <paramref name="radius"/> world units and scaled by <paramref name="intensity"/>. <paramref name="color"/>
        /// is the light's RGB (alpha ignored). Cleared each <see cref="Begin"/> like the instance queue.
        /// </summary>
        /// <remarks>
        /// Presentation only - never feed simulation/collision state from a light. Only the first
        /// <see cref="MaxPointLights"/> lights queued in a frame are uploaded (extras are dropped); pick the
        /// N nearest to the camera/action per frame so a dense scene stays within the GPU budget. Zero lights ==
        /// the historical key+fill+ambient render, bit-identical.
        /// </remarks>
        public void AddLight(Vector3 worldPos, Color color, float radius, float intensity)
        {
            Vector4 c = color;
            _lights.Add(new ModelRenderer.PointLightData
            {
                PosRadius = new Vector4(worldPos, radius),
                ColorIntensity = new Vector4(c.X, c.Y, c.Z, intensity),
            });
        }

        /// <summary>Count of point lights queued this frame (before the renderer's <see cref="MaxPointLights"/>
        /// clamp). Internal: lets tests assert <see cref="Begin"/> clears the queue and <see cref="AddLight"/>
        /// enqueues.</summary>
        internal int LightCount => _lights.Count;

        // ---- Debug line overlay (immediate-mode; queued this frame, drawn on top after post). ----

        /// <summary>Queue a single debug line from <paramref name="a"/> to <paramref name="b"/> in colour
        /// <paramref name="color"/> (RGBA). Cleared in <see cref="Begin"/>; drawn over the post image.</summary>
        public void DebugLine(Vector3 a, Vector3 b, Color color)
        {
            // Exception queue (class doc, Scene3D.RenderOrigin.cs): reduced here at submission since nothing reads it CPU-side.
            _lineVerts.Add(new LineRenderer.LineVertex(ToRender(a), color));
            _lineVerts.Add(new LineRenderer.LineVertex(ToRender(b), color));
        }

        /// <summary>Queue a ray from <paramref name="origin"/> along <paramref name="direction"/> for
        /// <paramref name="length"/> units.</summary>
        public void DebugRay(Vector3 origin, Vector3 direction, float length, Color color)
        {
            if (direction.LengthSquared() < 1e-12f) return;   // degenerate direction: nothing to draw
            DebugLine(origin, origin + Vector3.Normalize(direction) * length, color);
        }

        /// <summary>Queue the 12 edges of an axis-aligned box centred at <paramref name="center"/> with full
        /// extents <paramref name="size"/>.</summary>
        public void DebugBox(Vector3 center, Vector3 size, Color color)
        {
            _scratch.Clear();
            DebugShapes.Box(_scratch, center, size);
            AppendScratch(color);
        }

        /// <summary>Queue an XZ-plane grid through <paramref name="center"/>.Y: <c>cells+1</c> lines each way,
        /// spanning <c>cells*cellSize</c>.</summary>
        public void DebugGrid(Vector3 center, float cellSize, int cells, Color color)
        {
            _scratch.Clear();
            DebugShapes.Grid(_scratch, center, cellSize, cells);
            AppendScratch(color);
        }

        /// <summary>Queue 3 axis lines from <paramref name="origin"/> (X red, Y green, Z blue), each
        /// <paramref name="scale"/> long.</summary>
        public void DebugAxes(Vector3 origin, float scale)
        {
            DebugLine(origin, origin + new Vector3(scale, 0, 0), new Color(1f, 0.2f, 0.2f, 1f));
            DebugLine(origin, origin + new Vector3(0, scale, 0), new Color(0.2f, 1f, 0.2f, 1f));
            DebugLine(origin, origin + new Vector3(0, 0, scale), new Color(0.3f, 0.5f, 1f, 1f));
        }

        /// <summary>Queue a circle of <paramref name="segments"/> segments at <paramref name="radius"/> from
        /// <paramref name="center"/> in the plane perpendicular to <paramref name="normal"/>
        /// (use <see cref="Vector3.UnitY"/> for a ground ring).</summary>
        public void DebugCircle(Vector3 center, Vector3 normal, float radius, Color color, int segments = 32)
        {
            _scratch.Clear();
            DebugShapes.Circle(_scratch, center, normal, radius, segments);
            AppendScratch(color);
        }

        readonly List<Vector3> _scratch = new();

        void AppendScratch(Vector4 color)
        {
            foreach (var p in _scratch)
                _lineVerts.Add(new LineRenderer.LineVertex(ToRender(p), color));   // exception queue, see the class doc: reduced at submission, nothing reads it CPU-side
        }

        // ---- Debug wire VOLUMES (immediate-mode): closed 3D wire shapes for tuning gameplay volumes in-world
        //      (an NPC's aggro sphere, an attack dome/cylinder, a trigger radius). Default depth-tested so scene
        //      geometry occludes the buried parts. Pass DebugDepthMode.AlwaysOnTop for a crisp always-visible outline.
        //      Cleared each Begin() (immediate mode). Colour + opacity per call, colours per volume type owned by the
        //      game. The curved parts use `segments` per full circle. The structural line counts are the documented
        //      constants below. ----

        /// <summary>Default segment count for the curved rings and arcs of the debug wire volumes
        /// (<see cref="DebugWireSphere"/> / <see cref="DebugWireDome"/> / <see cref="DebugWireCylinder"/> /
        /// <see cref="DebugWireCircle"/>). 32 segments per full circle reads smooth at gameplay camera distances
        /// (a few metres of radius seen from ~10-30 m). Pass fewer for far or throwaway volumes.</summary>
        public const int DebugWireSegments = 32;

        // Structural line counts for the wire volumes: fixed so a screenful of overlapping NPC volumes stays cheap
        // while still reading clearly as the intended shape. Documented constants, deliberately not per-call knobs.
        const int WireSphereMeridians = 12;   // vertical pole-to-pole half-circle arcs
        const int WireSphereParallels = 5;    // horizontal latitude rings (odd => one lands on the equator)
        const int WireDomeMeridians = 12;     // apex-to-equator quarter arcs
        const int WireDomeParallels = 4;      // rings apex..equator (the last is the base equator circle)
        const int WireCylinderVerticals = 8;  // side lines joining the two rim circles

        /// <summary>Queue a wireframe sphere centred at <paramref name="center"/> with the given
        /// <paramref name="radius"/> in RGBA <paramref name="color"/> (e.g. an NPC's spherical aggro/attack radius).
        /// <paramref name="opacity"/> (0..1) scales the colour's alpha. <paramref name="depth"/> selects in-world
        /// depth-tested (default) vs always-on-top compositing, and <paramref name="segments"/> is the per-circle
        /// roundness. Immediate mode: cleared each <see cref="Begin"/>.</summary>
        public void DebugWireSphere(Vector3 center, float radius, Color color, float opacity = 1f,
            DebugDepthMode depth = DebugDepthMode.DepthTested, int segments = DebugWireSegments)
        {
            _scratch.Clear();
            DebugShapes.Sphere(_scratch, center, radius, WireSphereMeridians, WireSphereParallels, segments);
            AppendWireScratch(color, opacity, depth);
        }

        /// <summary>Queue a wireframe hemisphere DOME (flat side down) sitting on the XZ plane at
        /// <paramref name="baseCenter"/> and bulging up to <paramref name="radius"/> in Y, in RGBA
        /// <paramref name="color"/> (a hemispherical aggro/attack volume). The wire is meridian arcs plus latitude
        /// rings including the base equator circle. <paramref name="opacity"/> (0..1) scales the colour's alpha,
        /// <paramref name="depth"/> selects the compositing mode, <paramref name="segments"/> the per-circle
        /// roundness. Immediate mode.</summary>
        public void DebugWireDome(Vector3 baseCenter, float radius, Color color, float opacity = 1f,
            DebugDepthMode depth = DebugDepthMode.DepthTested, int segments = DebugWireSegments)
        {
            _scratch.Clear();
            DebugShapes.Dome(_scratch, baseCenter, radius, WireDomeMeridians, WireDomeParallels, segments);
            AppendWireScratch(color, opacity, depth);
        }

        /// <summary>Queue a wireframe vertical cylinder (axis along +Y) centred at <paramref name="center"/> with the
        /// given <paramref name="radius"/> and <paramref name="halfHeight"/> (half the total height), in RGBA
        /// <paramref name="color"/> (a columnar attack/trigger volume). Top and bottom rim circles joined by vertical
        /// side lines. <paramref name="opacity"/> (0..1) scales the colour's alpha, <paramref name="depth"/> selects
        /// the compositing mode, <paramref name="segments"/> the per-circle roundness. Immediate mode.</summary>
        public void DebugWireCylinder(Vector3 center, float radius, float halfHeight, Color color, float opacity = 1f,
            DebugDepthMode depth = DebugDepthMode.DepthTested, int segments = DebugWireSegments)
        {
            _scratch.Clear();
            DebugShapes.Cylinder(_scratch, center, radius, halfHeight, segments, WireCylinderVerticals);
            AppendWireScratch(color, opacity, depth);
        }

        /// <summary>Queue a wireframe circle of <paramref name="radius"/> at <paramref name="center"/> in the plane
        /// perpendicular to <paramref name="normal"/> (use <see cref="Vector3.UnitY"/> for a flat ground ring), in
        /// RGBA <paramref name="color"/>. The depth-aware sibling of <see cref="DebugCircle"/>: <paramref name="depth"/>
        /// selects in-world depth-tested (default) vs always-on-top, and <paramref name="opacity"/> (0..1) scales the
        /// colour's alpha. Immediate mode.</summary>
        public void DebugWireCircle(Vector3 center, Vector3 normal, float radius, Color color, float opacity = 1f,
            DebugDepthMode depth = DebugDepthMode.DepthTested, int segments = DebugWireSegments)
        {
            _scratch.Clear();
            DebugShapes.Circle(_scratch, center, normal, radius, segments);
            AppendWireScratch(color, opacity, depth);
        }

        /// <summary>Append the current <see cref="_scratch"/> line endpoints to the depth-tested or always-on-top
        /// stream with <paramref name="color"/> scaled by <paramref name="opacity"/>. No heap allocation
        /// (<see cref="Color"/> is a value type).</summary>
        void AppendWireScratch(Color color, float opacity, DebugDepthMode depth)
        {
            Vector4 c = opacity >= 1f ? color : color.WithAlpha(color.A * opacity);
            var dst = depth == DebugDepthMode.DepthTested ? _depthLineVerts : _lineVerts;
            foreach (var p in _scratch)
                dst.Add(new LineRenderer.LineVertex(ToRender(p), c));   // exception queue, see the class doc: reduced at submission, nothing reads it CPU-side
        }

        /// <summary>Count of queued always-on-top debug-line vertices this frame (2 per segment). Internal: lets
        /// tests assert <see cref="Begin"/> clears the queue and the builders route by <see cref="DebugDepthMode"/>.</summary>
        internal int LineVertexCount => _lineVerts.Count;

        /// <summary>Count of queued depth-tested debug wire-volume vertices this frame (2 per segment). Internal:
        /// lets tests assert immediate-mode clearing and depth-mode routing.</summary>
        internal int DepthLineVertexCount => _depthLineVerts.Count;

        // ---- Filled (alpha-blended) overlay: flat, world-space translucent shapes painted on a plane (ground
        //      tiles, range/zone/AoE highlights). Queued this frame, drawn after post UNDER the debug lines so an
        //      outline reads crisp on top of a fill. The mesh pass is opaque, so a tinted plane mesh can't blend;
        //      these live here in the overlay pass. ----

        readonly List<Vector3> _fillScratch = new();

        /// <summary>Queue a flat translucent quad centred at <paramref name="center"/>, lying in the plane with the
        /// given <paramref name="normal"/>, its first in-plane axis along <paramref name="uAxis"/>.
        /// <paramref name="halfExtents"/>.X scales that axis, .Y the perpendicular one. <paramref name="color"/> is
        /// RGBA; its alpha is blended over the post image. Cleared in <see cref="Begin"/>; drawn under the debug
        /// lines.</summary>
        public void DebugFilledQuad(Vector3 center, Vector3 normal, Vector3 uAxis, Vector2 halfExtents, Color color)
        {
            _fillScratch.Clear();
            DebugFillShapes.FilledQuad(_fillScratch, center, normal, uAxis, halfExtents);
            AppendFillScratch(color);
        }

        /// <summary>Queue a flat translucent quad on the XZ ground plane (normal +Y, u axis +X) centred at
        /// <paramref name="center"/>, with the given <paramref name="halfExtents"/> (X along world X, Y along world
        /// Z) and RGBA <paramref name="color"/>.</summary>
        public void DebugFilledQuad(Vector3 center, Vector2 halfExtents, Color color) =>
            DebugFilledQuad(center, Vector3.UnitY, Vector3.UnitX, halfExtents, color);

        /// <summary>Queue a square translucent ground tile centred at <paramref name="center"/> on the XZ plane,
        /// half a <paramref name="halfSize"/> across each way, in RGBA <paramref name="color"/>. The board-tile
        /// convenience (range/coverage/AoE highlights).</summary>
        public void DebugFilledQuad(Vector3 center, float halfSize, Color color) =>
            DebugFilledQuad(center, Vector3.UnitY, Vector3.UnitX, new Vector2(halfSize, halfSize), color);

        /// <summary>Queue a flat translucent disc of <paramref name="segments"/> triangles at
        /// <paramref name="radius"/> from <paramref name="center"/>, in the plane perpendicular to
        /// <paramref name="normal"/> (use <see cref="Vector3.UnitY"/> for a ground disc), in RGBA
        /// <paramref name="color"/>.</summary>
        public void DebugFilledCircle(Vector3 center, Vector3 normal, float radius, Color color, int segments = 32)
        {
            _fillScratch.Clear();
            DebugFillShapes.FilledCircle(_fillScratch, center, normal, radius, segments);
            AppendFillScratch(color);
        }

        /// <summary>Queue a flat translucent triangle fan from <paramref name="center"/> out to an arbitrary,
        /// already-ordered boundary <paramref name="rim"/> (e.g. a turret's star-shaped line-of-sight area), in RGBA
        /// <paramref name="color"/>. When <paramref name="closed"/> (the default) the loop is sealed with a wrap
        /// triangle (center, rim[last], rim[0]); pass <c>false</c> for an open arc. Wind the rim CCW about the
        /// desired facing normal (use <see cref="Vector3.UnitY"/> for a ground fan, as with
        /// <see cref="DebugFilledCircle"/>). Cleared in <see cref="Begin"/>; drawn under the debug lines.</summary>
        public void DebugFilledFan(Vector3 center, IReadOnlyList<Vector3> rim, Color color, bool closed = true)
        {
            _fillScratch.Clear();
            DebugFillShapes.FilledFan(_fillScratch, center, rim, closed);
            AppendFillScratch(color);
        }

        void AppendFillScratch(Vector4 color)
        {
            foreach (var p in _fillScratch)
                _fillVerts.Add(new FillRenderer.FillVertex(ToRender(p), color));   // exception queue, see the class doc: reduced at submission, nothing reads it CPU-side
        }

        /// <summary>Count of queued filled-overlay vertices this frame (3 per triangle). Internal: lets tests
        /// assert <see cref="Begin"/> clears the queue and the builders queue the expected geometry.</summary>
        internal int FillVertexCount => _fillVerts.Count;

        /// <summary>Queue one generic shaped ground decal for this frame (painted onto the ground/terrain via the
        /// depth buffer, under the meshes, through the post chain). Presentation only; cleared in <see cref="Begin"/>.
        /// The telegraph wrappers build these from a style + progress.</summary>
        public void DrawGroundDecal(in GroundDecal decal) => _decals.Add(decal);

        /// <summary>Count of ground decals queued this frame. Internal: lets tests assert <see cref="Begin"/> clears
        /// the queue and <see cref="DrawGroundDecal"/> enqueues.</summary>
        internal int DecalCount => _decals.Count;

        /// <summary>
        /// Queue one blob-shadow request for this frame: a soft dark ground blob under a shadow caster at
        /// <paramref name="blob"/>'s position. Only rendered when the resolved shadow tier is
        /// <see cref="ShadowMode.Blob"/> (set <c>Post.Quality.Shadows.Mode = ShadowMode.Blob</c>); with
        /// <see cref="ShadowMode.Off"/> the queue is ignored, so existing scenes are byte-stable. Presentation only;
        /// cleared in <see cref="Begin"/>. The scene layer submits one per caster it wants grounded (typically each
        /// character; props opt in by calling this with their footprint). Radius follows the caster's footprint and
        /// strength fades with <see cref="ShadowBlob.HeightAboveGround"/> per
        /// <see cref="ShadowSettings.BlobFadeHeight"/>.
        /// </summary>
        public void AddShadowBlob(in ShadowBlob blob) => _shadowBlobs.Add(blob);

        /// <summary>Count of blob-shadow requests queued this frame. Internal: lets tests assert <see cref="Begin"/>
        /// clears the queue and <see cref="AddShadowBlob"/> enqueues.</summary>
        internal int ShadowBlobCount => _shadowBlobs.Count;

        /// <summary>The shadow tier that will actually render this frame, resolved against the device (see
        /// <see cref="ShadowSettings.ResolveFor"/>). Internal: lets tests assert the degradation decision without a
        /// full render.</summary>
        internal ShadowResolution ResolvedShadows() => Post.Quality.Shadows.ResolveFor(_gd.Capabilities);

        /// <summary>The shadow tier that will actually render this frame (see <see cref="ResolvedShadows"/>'s
        /// <see cref="ShadowResolution.Effective"/>). Public so a consumer outside this assembly - a prop layer
        /// deciding whether to register a <see cref="ShadowBlob"/> (issue #388), or a game's own diagnostics -
        /// can gate cheaply on the resolved tier without re-deriving <see cref="ShadowSettings.ResolveFor"/>'s
        /// degradation policy itself.</summary>
        public ShadowMode ResolvedShadowMode => ResolvedShadows().Effective;

        /// <summary>
        /// Queue one stylized water surface for this frame, over <paramref name="plane"/>'s still-water height and XZ footprint,
        /// drawn after the sky + ground decals: a Gerstner swell displacing the surface grid, a domain-warped non-tiling ripple
        /// normal field with a distance detail fade, per-channel depth absorption blended toward the analytically reflected sky
        /// by fresnel, a GGX sun glint, whitecap + shoreline foam, and a shore fade (knobs: <see cref="WaterSettings"/> on <see cref="Post"/>). Opt-in: no <see cref="DrawWater(in WaterPlane)"/> call this frame means the water pass
        /// (<see cref="Rendering.WaterRenderer"/>) never runs, so existing scenes stay byte-stable. Presentation
        /// only; cleared in <see cref="Begin"/>. Call once per frame per distinct body of water (a game with several
        /// separate lakes/ponds queues one <see cref="WaterPlane"/> each).
        /// </summary>
        public void DrawWater(in WaterPlane plane) => _waterPlanes.Add(plane);

        /// <summary>Count of water planes queued this frame. Internal: lets tests assert <see cref="Begin"/> clears
        /// the queue and <see cref="DrawWater(in WaterPlane)"/> enqueues.</summary>
        internal int WaterPlaneCount => _waterPlanes.Count;

        /// <summary>Queue a translucent, UNLIT, depth-TESTED (not depth-writing) overlay draw of an already-loaded
        /// <paramref name="mesh"/> at world transform <paramref name="world"/> for this frame. The mesh's own
        /// per-vertex <see cref="ModelVertex.Color"/> (RGBA) supplies the colour and alpha, alpha-blended over the
        /// scene. It is occluded by nearer scene geometry (depth test) but never writes depth, so it never hides the
        /// scene. Drawn after the meshes/beams and before the pixel post, so it flows through the post chain like the
        /// rest of the model pass. A reusable overlay primitive: the collision-shape overlay is the first consumer;
        /// nav / AoI / chunk-bounds layers reuse it. Presentation only; cleared in <see cref="Begin"/>.
        /// Because depth-write is off, overlapping overlay meshes have no per-fragment depth ordering; the renderer
        /// sorts the queued proxies back-to-front by their world-origin view depth before drawing, so overlapping
        /// translucent proxies composite far-to-near regardless of submission order (a coarse per-draw sort, not
        /// per-fragment: two proxies that interpenetrate at a shared depth can still blend by their origin order).</summary>
        public void DrawOverlayMesh(MeshHandle mesh, Matrix4x4 world) => _overlayMeshDraws.Add((mesh, world));

        /// <summary>Count of overlay-mesh draws queued this frame. Internal: lets tests assert <see cref="Begin"/>
        /// clears the queue and <see cref="DrawOverlayMesh"/> enqueues.</summary>
        internal int OverlayMeshDrawCount => _overlayMeshDraws.Count;

        // ---- Camera-facing billboard overlay (immediate-mode; queued this frame, drawn on top after lines). ----

        /// <summary>Queue a camera-facing soft-disc billboard centred at <paramref name="worldPos"/> with half-size
        /// <paramref name="size"/> (the quad spans 2*size across), tinted by <paramref name="color"/> (RGBA), using
        /// the given <paramref name="blend"/>. Cleared in <see cref="Begin"/>; drawn over the post image and the
        /// debug lines. The game loops its particle system's <c>Active</c> span and calls this per particle. This is
        /// the LEGACY particle path (unoccluded overlay, uniform soft disc): new effects should prefer the modern
        /// <see cref="DrawParticle(in ParticleSprite)"/> pass, which is depth-tested, soft-faded against geometry,
        /// shaped procedurally, and feeds bloom. This overlay remains fully supported for crisp always-on-top
        /// markers.</summary>
        public void DrawBillboard(Vector3 worldPos, float size, Color color, BillboardBlend blend = BillboardBlend.Alpha)
        {
            // Alpha billboards defer to a back-to-front sort at render time (overlapping alpha must composite
            // far-to-near), so only the centre/size/colour is queued now; the vertex stream is built after sorting.
            if (blend == BillboardBlend.Alpha)
            {
                _billboardAlphaItems.Add(new BillboardItem { Center = worldPos, Size = size, Color = color });
                return;
            }

            // Additive billboards (sparks, muzzle flashes) are order-independent, so they expand straight to the
            // vertex stream in submission order and skip the sort. Camera basis is constant across a frame's
            // billboards; compute it once (on the first call) and reuse.
            if (!_billboardBasisValid)
            {
                BillboardGeometry.CameraBasis(ActiveCamera.Forward, out _billboardRight, out _billboardUp);
                _billboardBasisValid = true;
            }
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            BillboardGeometry.Triangles(ToRender(worldPos), size, _billboardRight, _billboardUp, pos, uv);   // exception queue, see the class doc: reduced at submission, nothing reads it CPU-side
            for (int i = 0; i < 6; i++)
                _billboardAdditive.Add(new BillboardRenderer.BillboardVertex(pos[i], uv[i], color));
        }

        /// <summary>
        /// Queue a camera-facing TEXTURED billboard: a quad at <paramref name="worldPos"/> with half-size
        /// <paramref name="size"/> (spans 2*size across), sampling the sub-rect <paramref name="sourceUv"/>
        /// (<c>(u0,v0,u1,v1)</c> - bottom-left to top-right; pass <c>(0,0,1,1)</c> for the whole texture, or a frame
        /// rect for a sprite sheet) of the texture loaded as <paramref name="texture"/>, multiplied by
        /// <paramref name="tint"/> (RGBA), using <paramref name="blend"/>. Cleared in <see cref="Begin"/>.
        /// </summary>
        /// <remarks>
        /// Unlike the colour-only <see cref="DrawBillboard(Vector3,float,Color,BillboardBlend)"/> (an overlay drawn
        /// after the post chain), textured billboards draw INTO the model pass with the depth test on (no depth
        /// write): a nearer mesh occludes the quad and the quad draws over a farther mesh, so meshes and sprites
        /// interleave correctly. Depth write is off, so overlapping quads have no per-fragment ordering; the renderer
        /// sorts the queued quads back-to-front by view depth before drawing, so overlapping alpha quads composite
        /// far-to-near regardless of the order you queue them. An invalid/<c>default</c> <paramref name="texture"/>
        /// draws nothing (no throw). Presentation only.
        /// </remarks>
        public void DrawBillboard(TextureHandle texture, Vector3 worldPos, float size, Vector4 sourceUv, Color tint,
            BillboardBlend blend = BillboardBlend.Alpha)
        {
            if (!texture.IsValid) return;   // nothing to sample: no-op, like the untextured-mesh fallback
            Vector4 c = tint;
            _texBillboardItems.Add(new TexturedBillboardItem
            {
                TexIndex = texture.ListIndex,
                Blend = blend,
                Center = worldPos,
                Size = size,
                SourceUv = sourceUv,
                Color = c,
            });
        }

        /// <summary>Queue a textured billboard sampling the WHOLE texture (source rect <c>(0,0,1,1)</c>); see
        /// <see cref="DrawBillboard(TextureHandle,Vector3,float,Vector4,Color,BillboardBlend)"/>.</summary>
        public void DrawBillboard(TextureHandle texture, Vector3 worldPos, float size, Color tint,
            BillboardBlend blend = BillboardBlend.Alpha) =>
            DrawBillboard(texture, worldPos, size, new Vector4(0f, 0f, 1f, 1f), tint, blend);

        /// <summary>Count of textured billboards queued this frame. Internal: lets tests assert
        /// <see cref="Begin"/> clears the queue and the overloads enqueue.</summary>
        internal int TexturedBillboardCount => _texBillboardItems.Count;

        // ---- Modern particle sprites: procedural SDF/noise shapes, depth-tested + soft-faded, premultiplied
        //      single-stream compositing, drawn before the post chain (additive glow feeds bloom). ----

        /// <summary>
        /// Queue one modern particle <paramref name="sprite"/> for this frame. The whole queue renders as ONE
        /// instanced draw after the water pass: back-to-front sorted, depth-tested against the scene (no write),
        /// soft-faded where it approaches geometry (<see cref="ParticleSoftFade"/>), shaped procedurally in the
        /// fragment shader (<see cref="ParticleShape"/>), at internal resolution BEFORE the post chain, so additive
        /// sprites feed bloom and every sprite flows through the pixel post like meshes. Alpha and additive sprites
        /// interleave correctly in the one sorted stream (premultiplied compositing). Cleared in <see cref="Begin"/>.
        /// The game loops its particle system's <c>Active</c> span and queues one sprite per particle, or uses the
        /// KhaozEngine.Particles.Render3D adapter package's turn-key extensions.
        /// </summary>
        public void DrawParticle(in ParticleSprite sprite) => _particleSprites.Add(sprite);

        /// <summary>Queue a batch of modern particle sprites, see <see cref="DrawParticle(in ParticleSprite)"/>.</summary>
        public void DrawParticles(ReadOnlySpan<ParticleSprite> sprites)
        {
            for (int i = 0; i < sprites.Length; i++) _particleSprites.Add(sprites[i]);
        }

        /// <summary>Count of modern particle sprites queued this frame. Internal: lets tests assert
        /// <see cref="Begin"/> clears the queue and the draw methods enqueue.</summary>
        internal int ParticleSpriteCount => _particleSprites.Count;

        /// <summary>Queue one screen-space distortion sprite (heat haze, refractive ring, splash lens). The whole
        /// queue accumulates into a lazily allocated half/quarter-res offset field the post chain's FIRST pass
        /// re-samples the scene colour through, so refraction precedes every camera-response pass. Depth-occluded
        /// against the scene like <see cref="DrawParticle(in ParticleSprite)"/>. Cleared in <see cref="Begin"/>. A
        /// frame that queues none allocates nothing and renders byte-identically to before distortion existed.
        /// Gated by <see cref="DistortionQuality"/>.</summary>
        public void DrawDistortion(in DistortionSprite sprite) => _distortionSprites.Add(sprite);

        /// <summary>Queue a batch of screen-space distortion sprites, see <see cref="DrawDistortion(in DistortionSprite)"/>.</summary>
        public void DrawDistortions(ReadOnlySpan<DistortionSprite> sprites)
        {
            for (int i = 0; i < sprites.Length; i++) _distortionSprites.Add(sprites[i]);
        }

        /// <summary>Count of distortion sprites queued this frame. Internal: lets tests assert <see cref="Begin"/>
        /// clears the queue and the draw methods enqueue.</summary>
        internal int DistortionSpriteCount => _distortionSprites.Count;

        // ---- Glowing beams (lasers/thrusters/tethers): a camera-facing strip a->b, additive, depth-interleaved
        //      into the model pass so geometry occludes it. Soft core+halo + optional taper/pulse/scroll in the
        //      fragment shader; animation reads EffectTimeSeconds. ----

        /// <summary>
        /// Queue an additive glowing beam from <paramref name="a"/> to <paramref name="b"/> (world points),
        /// <paramref name="width"/> world units across (the quad spans <paramref name="width"/>, i.e. ±width/2 from
        /// the axis), tinted by <paramref name="color"/> (the core colour unless <paramref name="style"/> overrides
        /// it). A camera-facing strip with a bright core + soft halo; optional end taper and time-driven pulse/scroll
        /// come from <paramref name="style"/> (null =&gt; <see cref="BeamStyle.Default"/>) and
        /// <see cref="EffectTimeSeconds"/>. Drawn INTO the model pass with the depth test on (no write), like the
        /// textured billboard, so a nearer mesh occludes the beam. Cleared in <see cref="Begin"/>. A degenerate beam
        /// (<paramref name="a"/>≈<paramref name="b"/> or <paramref name="width"/> &lt;= 0) is a silent no-op.
        /// Presentation only.
        /// </summary>
        public void DrawBeam(Vector3 a, Vector3 b, float width, Color color, BeamStyle? style = null)
        {
            if (width <= 0f || (b - a).LengthSquared() < 1e-12f) return;   // degenerate: nothing to draw
            BeamStyle s = style ?? BeamStyle.Default;
            Vector4 core = s.CoreColor ?? color;
            Vector4 glow = s.GlowColor is Color g ? g : new Vector4(core.X, core.Y, core.Z, core.W * 0.4f);
            _beamItems.Add(new BeamItem
            {
                A = a, B = b, Width = width,
                CoreColor = core,
                GlowColor = glow,
                Shape = new Vector4(s.CoreFraction, s.GlowSoftness, s.Taper, 0f),
                Anim = new Vector4(s.PulseSpeed, s.PulseAmount, s.ScrollSpeed, 0f),
            });
        }

        /// <summary>Count of beams queued this frame. Internal: lets tests assert <see cref="Begin"/> clears the
        /// queue and <see cref="DrawBeam"/> enqueues.</summary>
        internal int BeamCount => _beamItems.Count;

        /// <summary>The beams queued this frame (resolved colours/params). Internal: lets tests assert colour
        /// resolution.</summary>
        internal IReadOnlyList<BeamItem> BeamItems => _beamItems;

        // ---- Motion trails (weapon swings, thruster streaks, tracers): a tapered ribbon traced through an ordered
        //      list of recent world-space samples, camera-facing (or per-sample twist), depth-interleaved into the
        //      model pass like the beams. Immediate-mode: rebuilt each frame from the sample list (TrailGeometry). ----

        /// <summary>
        /// Queue one motion-trail ribbon for this frame from <paramref name="samples"/> (ordered oldest-first, i.e.
        /// tail to head - e.g. the last ~0.3s of a sword tip's world positions from a <see cref="Primitives.TrailSampler"/>).
        /// The engine builds a tapered, mitered strip whose across-direction faces the camera (or follows each
        /// sample's <see cref="TrailSample.Facing"/> when set), with alpha fading down the tail, and draws it INTO the
        /// model pass with the depth test on (no write) like <see cref="DrawBeam"/>, using <paramref name="style"/>'s
        /// tint/blend/soft-edge. Fewer than 2 samples is a silent no-op. Cleared in <see cref="Begin"/>. The samples
        /// are copied at call time (the span need not outlive the call). Presentation only.
        /// </summary>
        public void DrawTrail(ReadOnlySpan<TrailSample> samples, TrailStyle style)
        {
            if (samples.Length < 2) return;   // need at least one segment
            int start = _trailSamples.Count;
            for (int i = 0; i < samples.Length; i++)
                _trailSamples.Add(samples[i]);
            _trailItems.Add(new TrailItem { Start = start, Count = samples.Length, Style = style });
        }

        /// <summary>Count of trails queued this frame. Internal: lets tests assert <see cref="Begin"/> clears the
        /// queue and <see cref="DrawTrail"/> enqueues.</summary>
        internal int TrailCount => _trailItems.Count;

        /// <summary>The trails queued this frame (sample span + style). Internal: lets tests assert capture.</summary>
        internal IReadOnlyList<TrailItem> TrailItems => _trailItems;

        // Current internal render-target size (physical pixels). Exposed for tests to assert MatchViewport resizes
        // and FixedInternal stays put; not part of the public surface.
        internal int RenderTargetWidth => _res.Width;
        internal int RenderTargetHeight => _res.Height;

        // Bloom half-res target state. Exposed for tests to assert bloom off allocates nothing, bloom on allocates
        // exactly BloomMath.HalfResSize(RenderTargetWidth, RenderTargetHeight), and a resize/RenderScale change
        // re-derives it; not part of the public surface.
        internal bool BloomAllocated => _res.BloomAllocated;
        internal int BloomTargetWidth => _res.BloomWidth;
        internal int BloomTargetHeight => _res.BloomHeight;

        /// <summary>
        /// The internal render-target size for a given post config + viewport. <see cref="RenderScale.FixedInternal"/>
        /// returns <see cref="PixelPostProcessSettings.RenderWidth"/>/<c>RenderHeight</c> unchanged (the historical
        /// path). <see cref="RenderScale.MatchViewport"/> tracks the viewport, clamped to
        /// <see cref="PixelPostProcessSettings.MaxRenderWidth"/>/<c>MaxRenderHeight</c> with aspect preserved, each
        /// dimension at least 1. Pure + headless-testable (no GPU). Stable once the viewport is at/over the cap for a
        /// fixed aspect, so <see cref="EnsureSize"/> doesn't thrash.
        /// </summary>
        internal static (int W, int H) ComputeTargetSize(PixelPostProcessSettings s, int viewportW, int viewportH)
        {
            // Read the AA-resolved sizing (AntiAliasing.Ssaa forces MatchViewport + its factor); AntiAliasing.Off
            // leaves these equal to the raw RenderScale/Supersample fields, so existing callers are unchanged.
            if (s.EffectiveRenderScale == RenderScale.FixedInternal)
                return (s.RenderWidth, s.RenderHeight);

            // MatchViewport: render at the framebuffer size x the supersample factor (SSAA), capped
            // (aspect-preserving downscale) so a huge window / big factor doesn't allocate an unbounded target.
            // Guard against a zero/negative viewport during startup/minimise.
            float ss = MathF.Max(1f, s.EffectiveSupersample);
            int vw = Math.Max(1, (int)MathF.Round(Math.Max(1, viewportW) * ss));
            int vh = Math.Max(1, (int)MathF.Round(Math.Max(1, viewportH) * ss));
            int maxW = Math.Max(1, s.MaxRenderWidth);
            int maxH = Math.Max(1, s.MaxRenderHeight);
            if (vw <= maxW && vh <= maxH) return (vw, vh);
            float scale = ViewportMath.Fit(vw, vh, maxW, maxH);
            int w = Math.Max(1, (int)MathF.Round(vw * scale));
            int h = Math.Max(1, (int)MathF.Round(vh * scale));
            return (w, h);
        }

        /// <summary>
        /// Whether the final internal-target -> viewport blit is a genuine DOWNSCALE that should be filtered with a
        /// mip chain (a correct multi-tap box) rather than the historical single bilinear tap. True under
        /// <see cref="RenderScale.MatchViewport"/> supersampling (or a cap-forced downscale), and ALSO under
        /// <see cref="RenderScale.FixedInternal"/> when <see cref="PixelPostProcessSettings.MipFilterFixedInternalDownscale"/>
        /// is opted in and the window is smaller than the fixed internal target in either axis - both share the
        /// same "internal target strictly larger than the viewport" test, just gated by a different gate per scale
        /// mode. Always false with a Pixelated blit (retro stays single-mip point-sampled) or (for FixedInternal)
        /// when the opt-in flag is off, so every existing consumer and GPU golden stays byte-identical unless it
        /// deliberately opts in. Pure + headless-testable (no GPU). The mip fix is what makes
        /// <see cref="PixelPostProcessSettings.Supersample"/> correct at factors other than exactly 2 (a single
        /// bilinear tap under-samples above 2:1), and what fixes FixedInternal under-sampling on a window smaller
        /// than the fixed target when opted in.
        /// </summary>
        internal static bool WantsMipDownsample(PixelPostProcessSettings s, int viewportW, int viewportH)
        {
            if (s.Pixelated) return false;
            RenderScale scale = s.EffectiveRenderScale;
            bool eligible = scale == RenderScale.MatchViewport
                || (scale == RenderScale.FixedInternal && s.MipFilterFixedInternalDownscale);
            if (!eligible) return false;
            var (tw, th) = ComputeTargetSize(s, viewportW, viewportH);
            return tw > Math.Max(1, viewportW) || th > Math.Max(1, viewportH);
        }

        /// <summary>The anti-aliasing selection resolved against THIS device's capabilities (never throws): an MSAA
        /// request is clamped to <see cref="GpuCapabilities.MaxMsaaSampleCount"/> or falls back to FXAA if the device
        /// can't MSAA at all; SSAA/FXAA/None pass through. Read fresh each frame (Post is mutable).</summary>
        AntiAliasing ResolvedAa() => Post.EffectiveAaMode == AntiAliasingMode.None
            ? AntiAliasing.Off
            : Post.Quality.AntiAliasing.ResolveFor(_gd.Capabilities);

        /// <summary>The MSAA sample count actually used this frame (1 = off), after device clamping.</summary>
        int ResolvedMsaaSamples()
        {
            AntiAliasing aa = ResolvedAa();
            return aa.Mode == AntiAliasingMode.Msaa ? aa.MsaaSamples : 1;
        }

        // Rebuild the pipelines of every renderer that draws into the model MRT, so their sample count matches the
        // (possibly now multisampled) framebuffer. Called only when the MSAA sample count changes (rare - a menu
        // apply), never per frame. Material sets bind to each renderer's layout (not the pipeline), so loaded meshes
        // survive the rebuild.
        void RebuildMrtRenderers()
        {
            var modelOut = _res.ModelFB.Outputs;
            _model.SetOutputs(modelOut);
            _texBillboards.SetOutputs(modelOut);
            _beams.SetOutputs(modelOut);
            _trails.SetOutputs(modelOut);
            _overlayMeshes.SetOutputs(modelOut);
            _decalRenderer.SetOutputs(_res.ColorDepthFB.Outputs);
            _particleRenderer.SetOutputs(_res.ColorDepthFB.Outputs);
            _sky.SetOutputs(_res.ColorDepthFB.Outputs);
            _starfield.SetOutputs(_res.ColorDepthFB.Outputs);
            _water.SetOutputs(_res.ColorDepthFB.Outputs);
            _depthLines.SetOutputs(_res.ColorDepthFB.Outputs);
        }

        void EnsureSize(int viewportW, int viewportH)
        {
            var (tw, th) = ComputeTargetSize(Post, viewportW, viewportH);
            bool wantMips = WantsMipDownsample(Post, viewportW, viewportH);
            int samples = ResolvedMsaaSamples();
            bool sampleChanged = _res.SampleCount != samples;
            bool bloomChanged = _res.BloomAllocated != Post.Bloom.Enabled;
            bool hdrChanged = _res.HdrColor != Post.Hdr.Enabled;
            if (_res.Width != tw || _res.Height != th || _res.Mipped != wantMips || sampleChanged || bloomChanged || hdrChanged)
            {
                // A pipeline in flight may reference the old sample count / colour format / targets. A MSAA or HDR
                // toggle is rare, so idling before recreating the MRT + rebuilding pipelines is cheap insurance. An
                // HDR toggle changes the MRT colour attachment format, so every MRT-writing renderer's pipeline must
                // be rebuilt too (RebuildMrtRenderers), exactly like a sample-count change.
                if (sampleChanged || hdrChanged) _gd.WaitForIdle();
                _res.Resize(tw, th, wantMips, samples, Post.Bloom.Enabled, Post.Hdr.Enabled);
                _post.BindTargets(_res);
                _transitions.BindTargets(_res);
                if (sampleChanged || hdrChanged) RebuildMrtRenderers();   // match the renderers' pipelines to the new MRT sample count / colour format
            }
            // Aspect uses the true viewport (the post target is blit-stretched to fill it), not the clamped target.
            Camera.AspectRatio = viewportH > 0 ? (float)viewportW / viewportH : Camera.AspectRatio;
        }

        /// <summary>
        /// Fit this frame's <see cref="ShadowSettings.ResolvedCascadeCount"/> cascades (CPU-authored, NOT
        /// GPU-clip-corrected) into <see cref="_cascadeCpuVps"/> (render-relative) and
        /// <see cref="_cascadeCpuVpsAbsolute"/> (the CPU caster test's, see <see cref="FitCascade"/>), and return the
        /// count. Standard frustum-slice
        /// CSM: the active camera's frustum is split along VIEW DEPTH (near plane, <see cref="ShadowSettings.ShadowNearDistance"/>,
        /// ... , <see cref="ShadowSettings.ResolvedMaxDistance"/> via the practical split) and each cascade
        /// frames its slice's bounding sphere, texel-snapped. Texel density therefore follows what is ON
        /// SCREEN: a visible caster always samples from the tightest cascade covering its view depth, whatever
        /// the camera is looking at (the old gaze-point focus made shadow sharpness depend on the camera's
        /// look direction and jumped when the gaze ray left the ground plane). Factored out of
        /// <see cref="RenderShadowDepthPass"/> so the same matrices drive the depth pass, the receiver tail
        /// AND the caster-visibility test (which unions ALL cascades: under a slice fit no single cascade
        /// bounds the rest). Returns 0 for a degenerate (non-invertible) camera, and the caller skips shadows
        /// that frame.
        /// </summary>
        int ComputeShadowCascades()
        {
            var shadows = Post.Quality.Shadows;
            // ABSOLUTE: the fit, the radii and the caster classification stay byte-identical at any render origin.
            if (!Internal.ShadowMapMath.FrustumCornersWorld(FrameAbsoluteViewProjection(), _frustumCornersScratch))
            {
                _cascadeCount = 0;
                return 0;
            }
            Vector3 eye = ActiveCamera.Eye;
            Vector3 fwd = ActiveCamera.Forward;
            // View depths of the near/far planes read off the unprojected corners (camera-type-agnostic: no
            // reliance on perspective-projection matrix fields, so iso/ortho cameras fit identically).
            Vector3 nearC = (_frustumCornersScratch[0] + _frustumCornersScratch[1] + _frustumCornersScratch[2] + _frustumCornersScratch[3]) * 0.25f;
            Vector3 farC = (_frustumCornersScratch[4] + _frustumCornersScratch[5] + _frustumCornersScratch[6] + _frustumCornersScratch[7]) * 0.25f;
            float camNear = Vector3.Dot(nearC - eye, fwd);
            float camFar = Vector3.Dot(farC - eye, fwd);
            float range = MathF.Max(camFar - camNear, 1e-3f);

            int res = _model.ShadowMap.Resolution;         // the actual allocated per-cascade resolution (clamped)
            int count = _model.ShadowMap.CascadeCount;     // the actual allocated cascade count (clamped)
            Span<float> splits = stackalloc float[ShadowSettings.MaxCascades];
            Internal.ShadowMapMath.FillCascadeSplits(splits, count, shadows.ShadowNearDistance, shadows.ResolvedMaxDistance);
            Vector3 lightDir = Vector3.Normalize(Post.LightDirection);
            float prev = camNear;
            for (int i = 0; i < count; i++)
            {
                float d = Math.Clamp(splits[i], camNear, camFar);
                Internal.ShadowMapMath.SliceBoundingSphere(_frustumCornersScratch,
                    (prev - camNear) / range, (d - camNear) / range, out Vector3 center, out float radius);
                FitCascade(i, lightDir, center, radius, res);
                _cascadeRadii[i] = radius;
                prev = MathF.Max(d, prev);
            }
            _cascadeCount = count;
            return count;
        }

        /// <summary>
        /// Set this frame's RECEIVER shadow tail on the model/splat frame UBO from the fitted cascades in
        /// <see cref="_cascadeCpuVps"/>: GPU-clip-correct each cascade (the depth pass renders to texture, same
        /// convention as the model pass's ViewProj) into <see cref="_cascadeReceiverVps"/>, bake the atlas-column
        /// transform onto each for the depth pass into <see cref="_cascadeDepthVps"/>, derive the per-cascade
        /// normal-offset world sizes + the fade params, and hand them to the model renderer (uploaded with the frame
        /// UBO in the model pass). Always called when the shadow-map tier is active - whether or not the atlas is
        /// re-rendered this frame (the dirty-skip reuses it) - so the receivers always sample with the matrices the
        /// atlas was baked against, and bias/strength changes apply even on a skipped frame.
        /// </summary>
        void SetShadowReceiverTail()
        {
            var shadows = Post.Quality.Shadows;
            int count = _cascadeCount;
            int res = _model.ShadowMap.Resolution;
            float texelStep = 1f / Math.Max(1, res);
            for (int i = 0; i < count; i++)
            {
                _cascadeReceiverVps[i] = GpuClip.Correct(_cascadeCpuVps[i], _gd.Capabilities);
                // Bake the atlas-column placement onto the depth matrix (there is no viewport): the receiver samples
                // with the plain per-cascade matrix and maps UV into the column itself.
                _cascadeDepthVps[i] = _cascadeReceiverVps[i] * Internal.ShadowMapMath.AtlasColumnTransform(i, count);
                // Per-cascade normal-offset world size = one cascade texel's world width (2*radius_i/res) x the tunable
                // ShadowNormalOffset (in texels), so far cascades (bigger texels) offset more and near ones less - the
                // 10.116.0 normal offset scaled per cascade, which keeps far cascades acne-free and near ones attached.
                // The radius is the FITTED slice-sphere radius (the split distances are no longer the ortho extents).
                float texelWorld = Internal.ShadowMapMath.TexelWorldSize(_cascadeRadii[i], res);
                _cascadeNormalOffsets[i] = texelWorld * MathF.Max(0f, shadows.ShadowNormalOffset);
                // Per-cascade dissolve noise scale (issue #391): the same texel world size floors the noise cell so a
                // dithered caster stays resolvable in this cascade instead of degenerating into isolated texels.
                _cascadeNoiseScales[i] = Internal.ShadowDissolveNoise.ScaleForCascade(_cascadeRadii[i], res);
            }
            // Outermost-cascade UV border fade width (fraction of the map) so the coverage edge fades to lit instead of
            // a hard box. maxDistance (the outer cascade's coverage reach = ShadowMaxDistance for count>1, else the
            // near distance) rides in the UBO as the documented coverage distance. The fade itself is UV-border-driven.
            float border = 0.12f;
            float maxDist = count > 1 ? shadows.ResolvedMaxDistance : shadows.ShadowNearDistance;
            float blend = Math.Clamp(shadows.ShadowCascadeBlend, 0f, 0.49f);
            _model.SetShadowUniforms(_cascadeReceiverVps.AsSpan(0, count), count, texelStep,
                shadows.ShadowConstantBias, shadows.ShadowSlopeBias, shadows.ShadowStrength,
                maxDist, border, blend, _cascadeNormalOffsets.AsSpan(0, count));
        }

        /// <summary>
        /// Record the scene (model pass over all queued instances -> post chain -> blit) into
        /// <paramref name="cl"/>, ending on <paramref name="target"/>. The caller owns Begin/End/Submit of
        /// <paramref name="cl"/>. <paramref name="viewportW"/>/<paramref name="viewportH"/> are the target size.
        /// </summary>
        internal void RenderInternal(IGpuCommandList cl, int viewportW, int viewportH, IGpuFramebuffer target)
        {
            // Reset the always-on frame counters here (not in Begin), so they are finalized per rendered frame the
            // same way DrawnInstances / PassTimingsMs are - a Begin without a render leaves the last render's totals,
            // and a re-render never double-counts.
            _frameStats.Reset();
            EnsureSize(viewportW, viewportH);
            // The caps-resolved FXAA decision (AntiAliasingMode.Fxaa, or an MSAA request the device couldn't honour
            // falling back to FXAA). Must be the same value for PrepareUniforms (flip parity) and Run.
            bool runFxaa = ResolvedAa().Mode == AntiAliasingMode.Fxaa;
            // Distortion is lazily allocated per frame (unlike bloom, which is a resize-time decision): whether any
            // distortion sprite is queued is known now (queues fill between Begin and Render). (Re)allocate the
            // offset field at half res (Full) or quarter res (Reduced) when sprites are queued, free it when none,
            // then rebind the post apply set over it if that bumped the target generation (a cheap early-out
            // otherwise). Byte-neutral when never used. The apply-pass parity is stable from here through Run.
            bool distortionActive = _distortionSprites.Count > 0;
            _res.EnsureDistortion(distortionActive, DistortionQuality == DistortionQuality.Full ? 2 : 4);
            _post.BindTargets(_res);
            // Edge pass needs the camera's depth convention (perspective vs ortho + near/far) to linearize depth
            // under perspective; derived from the projection matrix so no camera-interface change is required.
            var camDepth = Internal.OutlineMath.ExtractCameraDepth(ActiveCamera.Projection);
            _post.PrepareUniforms(cl, _res, Post, camDepth, runFxaa, distortionActive);

            // Frozen-frame capture for a screen crossfade must read the PREVIOUS frame (the origin view, before the
            // teleport cut). Snapshot ColorTex here, at the top of the frame, before the model pass overwrites it. No-op
            // unless a FrozenCrossfade transition just went active. See TransitionRenderer.BeginFrame.
            _transitions.BeginFrame(cl, _res, ScreenTransition);

            // Relative for the GPU, absolute for every CPU-side spatial computation, and the eye converted with the
            // geometry (the water/particle/distortion shaders difference it against a render-frame world position).
            Matrix4x4 vp = FrameViewProjection();
            Matrix4x4 absVp = FrameAbsoluteViewProjection();
            Vector3 eye = ToRender(ActiveCamera.Eye);

            // GPU instancing: group queued instances by mesh into a flat instance array (ordered by mesh) + a
            // run per unique mesh. Reuses member buffers (cleared, not realloc) to stay per-frame alloc-free. Done
            // BEFORE the model pass so the (optional) shadow depth pass can reuse the same uploaded instance buffer.
            GroupInstances(_instances.Items, _instanceData, _runs, _meshRunIndex, _instanceCastKinds);
            // Fold each mesh's alpha-cutout threshold into its instances' SpecParams.z (the model fragment discards
            // texels below it, so MASK foliage renders as its silhouette). A mesh with cutoff 0 (OPAQUE, the default)
            // is untouched, so the instance data - and the render - stays byte-identical to the pre-cutout path.
            ApplyAlphaCutoffs(_instanceData, _runs);
            // Reduced into a staging copy, so _instanceData stays absolute for the culling + caster reads below.
            UploadInstancesRelative(cl);

            // Camera-frustum visibility for the MAIN pass only. Computed after grouping (so it is index-aligned to
            // the uploaded _instanceData / runs) and BEFORE the shadow pass runs, but the shadow pass ignores it -
            // an off-screen caster must still write depth into the light-space map so its shadow lands on-screen.
            // Reuses _instanceVisible (grown, not per-frame allocated); the main + splat draws then rasterize only
            // the visible contiguous sub-spans of each run against the same GPU buffer (no re-upload, no reorder).
            FrustumPlanes camFrustum = FrustumCulling ? FrustumPlanes.Extract(absVp) : default;
            ComputeMainPassVisibility(camFrustum);

            // Resolve the shadow tier + (when active) this frame's cascade fit BEFORE the CPU skin pass below, so an
            // off-camera skinned draw's shadow-caster visibility can be decided up front (see
            // ClassifySkinnedVisibility): a character camera-culled from the main pass but still inside the shadow
            // volume must still be CPU-skinned so its shadow lands on-screen. RenderShadowDepthPass (below) reuses the
            // fitted cascades instead of recomputing them, so the two passes can never disagree on the fit. Under the
            // frustum-slice fit no single cascade bounds the rest, so the caster-visibility test unions ALL cascades'
            // frustums (extracted here), and a degenerate camera (count 0) drops shadows for the frame.
            bool shadowMapActive = Post.Quality.Shadows.ResolveFor(_gd.Capabilities).Effective == ShadowMode.ShadowMap;
            int shadowCascadeCount = 0;
            if (shadowMapActive)
            {
                shadowCascadeCount = ComputeShadowCascades();
                if (shadowCascadeCount == 0) shadowMapActive = false;   // degenerate camera: no shadows this frame
                for (int i = 0; i < shadowCascadeCount; i++)
                    _shadowFrustums[i] = FrustumPlanes.Extract(_cascadeCpuVpsAbsolute[i]);
            }

            // CPU-skin each queued skinned draw into one concatenated stream + per-draw instance data (deformed on the
            // CPU because the GPU bone-buffer read corrupts past element 0 in the windowed Veldrid/Metal swapchain
            // context - only bones[0] survives; extensively bisected). Built here (before both passes) so the shadow
            // depth pass and the model pass share the uploaded skinned buffers. SkinningMath.SkinVertex mirrors the
            // shader blend exactly. A draw that is camera-culled AND (shadows off, or outside the shadow ortho
            // volume too) is skipped entirely here - no skin, no upload, no draw in either pass. See
            // ClassifySkinnedVisibility for why this can never drop a caster whose shadow would have been visible.
            // UseGpuSkinning (opt-in, default off) swaps the CPU deform for the GPU fold-matrix path: no per-frame
            // vertex skin/upload, only the per-draw combined-UBO slots (matrices + palette). Both paths share the exact
            // same visibility classification + counters, so DrawnSkinnedInstances / CulledSkinnedInstances match.
            var skinnedItems = _skinnedInstances.Items;
            _cpuSkinnedVerts.Clear();
            _cpuSkinnedInstances.Clear();
            _cpuSkinnedDraws.Clear();
            _gpuSkinnedDraws.Clear();
            _drawnSkinnedInstances = 0;
            _culledSkinnedInstances = 0;
            if (skinnedItems.Count > 0)
            {
                var boneSpan = CollectionsMarshal.AsSpan(_boneMatrices);
                const int cap = SkinningMath.MaxBonesPerDraw;
                for (int i = 0; i < skinnedItems.Count; i++)
                {
                    var it = skinnedItems[i];
                    if (!_skinnedSlots.IsValid(it.Mesh.Index, it.Mesh.Generation)) continue;
                    var entry = _skinnedMeshes[it.Mesh.Index];
                    if (entry is null) continue;
                    var src = _skinnedCpuVerts[it.Mesh.Index];
                    if (src is null) continue;

                    var (visibleMain, visibleShadow) = ClassifySkinnedVisibility(
                        entry.Bounds, it.World, FrustumCulling, camFrustum, shadowMapActive, _shadowFrustums.AsSpan(0, shadowCascadeCount));
                    if (!visibleMain && !visibleShadow) { _culledSkinnedInstances++; continue; }
                    if (visibleMain) _drawnSkinnedInstances++; else _culledSkinnedInstances++;

                    bool dissolving = it.Dissolving;
                    // During a dissolve the emissive channel carries the edge colour. SpecParams.z/.w carry the
                    // dissolve threshold + edge width (0 on a normal draw, so the values match the pre-dissolve path).
                    Vector4 emissive = dissolving ? it.DissolveEdge : it.Material.Emissive;
                    Vector4 specParams = dissolving
                        ? new Vector4(it.Material.Specular, it.Material.Shininess, it.DissolveThreshold, it.DissolveEdgeWidth)
                        : new Vector4(it.Material.Specular, it.Material.Shininess, 0f, 0f);

                    if (UseGpuSkinning)
                    {
                        // Record the draw. The GPU deforms the rest-pose buffer. The palette lives at slot i of
                        // _boneMatrices (submission index), packed into the combined UBO at the compacted slot below.
                        _gpuSkinnedDraws.Add(new GpuSkinnedDraw(entry.Vb, entry.Ib, entry.IndexCount, entry.IndexFormat,
                            entry.SkinnedMaterialSet, i * cap, entry.InverseBind.Length, (uint)_gpuSkinnedDraws.Count,
                            ToRender(it.World), it.Tint, emissive, specParams, visibleMain, dissolving));   // reduced after the absolute classify
                    }
                    else
                    {
                        int baseVertex = _cpuSkinnedVerts.Count;
                        var palette = boneSpan.Slice(i * cap, cap);   // this draw's composed bone window (slot i)
                        for (int v = 0; v < src.Length; v++)
                            _cpuSkinnedVerts.Add(SkinningMath.SkinVertex(src[v], palette));
                        _cpuSkinnedInstances.Add(new ModelRenderer.InstanceData
                        {
                            Model = ToRender(it.World),   // reduced AFTER the absolute visibility classification above
                            Tint = it.Tint,
                            Emissive = emissive,
                            SpecParams = specParams,
                            IsDynamic = 1f,   // skinned character: tag it so the main ground-decal pass rejects it (issue #235)
                        });
                        _cpuSkinnedDraws.Add(new CpuSkinnedDraw(entry.Ib, entry.IndexCount, entry.IndexFormat, baseVertex, entry.MaterialSet, dissolving, visibleMain));
                    }
                }
                if (UseGpuSkinning)
                {
                    if (_gpuSkinnedDraws.Count > 0)
                    {
                        _model.EnsureSkinnedMainCapacity((uint)_gpuSkinnedDraws.Count);
                        // One skinned-depth slot per (cascade, caster): each cascade folds its own light matrix.
                        if (shadowMapActive) _model.EnsureSkinnedShadowCapacity((uint)(_gpuSkinnedDraws.Count * Math.Max(1, _cascadeCount)));
                    }
                }
                else if (_cpuSkinnedDraws.Count > 0)
                {
                    _model.UploadCpuSkinned(cl, CollectionsMarshal.AsSpan(_cpuSkinnedVerts), CollectionsMarshal.AsSpan(_cpuSkinnedInstances));
                    _frameStats.AddSkinnedUpload((long)_cpuSkinnedVerts.Count * Unsafe.SizeOf<ModelVertex>()
                        + (long)_cpuSkinnedInstances.Count * Unsafe.SizeOf<ModelRenderer.InstanceData>());
                }
            }
            int skinnedCasterCount = UseGpuSkinning ? _gpuSkinnedDraws.Count : _cpuSkinnedDraws.Count;

            // Key-light cascaded shadow map (ShadowMode.ShadowMap): a depth-only pass over the SAME instanced casters
            // into the ortho light-space cascade atlas, BEFORE the model pass, so the model + splat fragments sample it.
            // Off/Blob leave the shadow tail at strength 0, so the frame is byte-stable (no depth pass, the shader never
            // taps the atlas). Set the shadow tail BEFORE SetFrameUniforms (which uploads the whole frame UBO incl. that
            // tail). shadowMapActive + the cascade fit were resolved above (before the CPU skin pass), so this reuses
            // the exact same matrices the skinned-visibility split was computed against.
            float shadowDepthMs = 0f, modelMs = 0f, transparentsMs = 0f, waterSyncMs = 0f, postMs = 0f;
            long timingStart = 0;
            _shadowPassSkippedLastFrame = false;
            _lastShadowPassDiagnostics = default;
            if (shadowMapActive)
            {
                timingStart = EnableTiming ? Stopwatch.GetTimestamp() : 0;
                // Always set the receiver tail so the model + splat fragments sample the atlas (whether or not its depth
                // is re-rendered this frame). Then decide whether the depth pass must actually re-run: the atlas
                // persists across frames, so an unchanged static scene reuses it and skips every caster draw.
                SetShadowReceiverTail();
                BuildShadowCasterSpans(_shadowCasterRunsScratch, _shadowCasterModelsScratch);
                bool hadPrevious = _shadowPassRendered;
                bool resolutionChanged = hadPrevious && _model.ShadowMap.Resolution != _lastShadowResolution;
                bool lightMatrixChanged = hadPrevious && ShadowCascadeVpsChanged(
                    _cascadeCpuVps, _cascadeCount, _lastCascadeCpuVps, _lastShadowCascadeCount);
                bool casterDataChanged = hadPrevious && ShadowCastersChanged(
                    _shadowCasterRunsScratch, _shadowCasterModelsScratch, _lastShadowCasterRuns, _lastShadowCasterModels);
                bool dirty = ShadowDepthPassDirty(
                    hadPrevious: hadPrevious,
                    anySkinnedCaster: skinnedCasterCount > 0,
                    resolutionChanged: resolutionChanged,
                    lightMatrixChanged: lightMatrixChanged,
                    casterDataChanged: casterDataChanged);
                if (dirty)
                {
                    // Split the one caster list into the sub-spans each cascade actually reaches, then draw. Only on
                    // a dirty pass: a skipped frame draws nothing, so it must not pay for the split either. The
                    // SIGNATURE above stays the FULL list on purpose - the cull is a pure function of the caster
                    // transforms (already in that signature) and the cascade matrices (already in the light-matrix
                    // compare), so it can never change the drawn set without one of those dirtying the pass first.
                    BuildCascadeCasterSpans(_cascadeCount);
                    RenderShadowDepthPass(cl, _cascadeCasterSpans);
                    // Commit this frame's signature as next frame's reference. Swap the reused buffers (no copy/alloc):
                    // the scratch now holds the just-rendered casters, so make it the kept copy and reuse the old kept
                    // copy as next frame's scratch.
                    (_lastShadowCasterRuns, _shadowCasterRunsScratch) = (_shadowCasterRunsScratch, _lastShadowCasterRuns);
                    (_lastShadowCasterModels, _shadowCasterModelsScratch) = (_shadowCasterModelsScratch, _lastShadowCasterModels);
                    Array.Copy(_cascadeCpuVps, _lastCascadeCpuVps, _cascadeCount);
                    _lastShadowCascadeCount = _cascadeCount;
                    _lastShadowResolution = _model.ShadowMap.Resolution;
                    _shadowPassRendered = true;
                }
                else
                    _shadowPassSkippedLastFrame = true;
                // Recorded AFTER the decision so one snapshot describes one frame: the per-cascade rigid span counts
                // and the raw draw calls only exist once the pass has walked them, and a skipped frame must report
                // zero of both rather than the last rendered pass's numbers (issue #410).
                RecordShadowPassDiagnostics(dirty, hadPrevious, skinnedCasterCount > 0, resolutionChanged,
                    lightMatrixChanged, casterDataChanged, skinnedCasterCount);
                if (EnableTiming) shadowDepthMs = ElapsedMs(timingStart);
            }
            else
                _model.ClearShadowUniforms();

            timingStart = EnableTiming ? Stopwatch.GetTimestamp() : 0;
            _model.BeginModelPass(cl, _res, Post);
            _model.SetFrameUniforms(cl, vp, eye, Post, CollectionsMarshal.AsSpan(_lights), _frameOrigin);
            _model.BindPass(cl);

            if (_instanceData.Count > 0)
            {
                foreach (var run in _runs)
                {
                    // Skip a run whose mesh was unloaded (stale handle): a destroyed entity may linger a frame.
                    // The instance data was uploaded contiguously, so skipping a run just leaves its slice undrawn.
                    if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                    var m = _meshes[run.Mesh.Index];
                    if (m is not { } mesh) continue;
                    if (mesh.SplatMaterial >= 0) continue;   // drawn in the splat pass below
                    // Draw only the visible contiguous sub-spans of this run (against the already-uploaded buffer).
                    uint spanStart = run.Start; uint spanLen = 0;
                    for (uint s = 0; s < run.Count; s++)
                    {
                        if (_instanceVisible[run.Start + s]) { if (spanLen == 0) spanStart = run.Start + s; spanLen++; }
                        else if (spanLen > 0) { _model.DrawMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, mesh.MaterialSet); CountMeshDraw(mesh.IndexCount, spanLen); spanLen = 0; }
                    }
                    if (spanLen > 0) { _model.DrawMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, mesh.MaterialSet); CountMeshDraw(mesh.IndexCount, spanLen); }
                }
                // Splat-terrain pass: same uploaded instance buffer, the dedicated 5-layer texture-array pipeline.
                // Each material's combined UBO holds frame + params in one buffer, so re-sync this frame's uniforms
                // into every loaded material's UBO before drawing (usually one terrain material).
                for (int i = 0; i < _splatMaterials.Count; i++)
                    if (_splatMaterials[i] is { } syncSm) _model.WriteFrameUniformsTo(cl, syncSm.Ubo);
                bool splatBound = false;
                foreach (var run in _runs)
                {
                    if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                    var m = _meshes[run.Mesh.Index];
                    if (m is not { } mesh) continue;
                    if (mesh.SplatMaterial < 0) continue;
                    var sm = _splatMaterials[mesh.SplatMaterial];
                    if (sm is null) continue;
                    if (!splatBound) { _model.BindSplatPass(cl); splatBound = true; }
                    uint spanStart = run.Start; uint spanLen = 0;
                    for (uint s = 0; s < run.Count; s++)
                    {
                        if (_instanceVisible[run.Start + s]) { if (spanLen == 0) spanStart = run.Start + s; spanLen++; }
                        else if (spanLen > 0) { _model.DrawSplatMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, sm.Set); CountMeshDraw(mesh.IndexCount, spanLen); spanLen = 0; }
                    }
                    if (spanLen > 0) { _model.DrawSplatMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, sm.Set); CountMeshDraw(mesh.IndexCount, spanLen); }
                }
            }

            // Blob shadows (receiver-only): with the resolved tier at Blob, turn each queued ShadowBlob into a dark
            // Circle GroundDecal and draw it HERE - after the opaque RECEIVER geometry (terrain/props/splat) wrote
            // depth, but BEFORE the skinned character pass below. Drawing before the characters makes each caster
            // opaquely occlude its OWN blob, so the ground-Y band (groundY-YTolerance .. groundY+MaxStep) is never
            // repainted onto the caster's own lower body (legs/shins) the way the old after-model placement was -
            // while the blob still conforms to terrain slopes across that band (no BlobGroundMaxStep clamp needed).
            // The decal frag reconstructs the surface from the resolved linear depth, so resolve it now (no-op single-
            // sample); the decal pass binds ColorDepthFB, so re-bind ModelFB + the model pipeline afterwards to continue
            // the pass with the skinned draws. Off / empty queue => skipped entirely, so a no-blob frame renders
            // byte-identical (same ModelFB binding, no extra resolve). ShadowMap resolves to itself on capable devices
            // (that branch stays idle); ResolveFor only lands here when the device lacks shadow-map support and
            // ShadowMap degrades to Blob.
            _shadowDecals.Clear();
            if (_shadowBlobs.Count > 0 && Post.Quality.Shadows.ResolveFor(_gd.Capabilities).Effective == ShadowMode.Blob)
            {
                var shadows = Post.Quality.Shadows;
                for (int i = 0; i < _shadowBlobs.Count; i++)
                    if (shadows.TryBuildDecal(_shadowBlobs[i], out GroundDecal blobDecal))
                        _shadowDecals.Add(blobDecal);
                if (_shadowDecals.Count > 0)
                {
                    // DEPTH ONLY, deliberately. This pass runs early, before the textured billboards / beams / trails /
                    // overlay meshes have written the MRT normal, so resolving the normal here would publish an
                    // incomplete one. Safe because a blob-shadow decal is engine-built (Shadows.TryBuildDecal) and
                    // never sets VoidFallback, so it never samples NormalTex. The MAIN decal pass, which sits after
                    // every normal writer, takes ResolveDepthNormal instead.
                    _res.ResolveDepth(cl);
                    // Batched decal pass: one instanced draw per blend run, so count the runs it issued (not a flat 1).
                    // Blob-shadow decals are legacy Solid fills (no pattern/energy/feather), so time+quality are inert here.
                    // rejectDynamicGeometry: false. This pass runs BEFORE the skinned draws and resolves only depth
                    // (not the normal target the reject reads), and a blob shadow wants no dynamic reject anyway.
                    _frameStats.DrawCalls += _decalRenderer.Draw(cl, _res, vp, EffectTimeSeconds, DecalQuality, Post.Hdr.Enabled, false, RelativeDecals(_shadowDecals));
                    cl.SetFramebuffer(_res.ModelFB);
                    _model.BindPass(cl);
                }
            }

            // Skinned draws: an entry with VisibleMain false is drawn only into the shadow map (camera-culled,
            // shadow-visible - see ClassifySkinnedVisibility), so it must NOT also draw here.
            if (UseGpuSkinning)
            {
                // GPU path: fold each visible-main draw's slot (Mvp = world * this frame's ViewProj, packed after
                // SetFrameUniforms so _frame.ViewProj is current), then draw through the skinned pipeline (rest-pose
                // buffer at slot 0, combined UBO window at set 0, material at set 1). Pack all, then bind + draw.
                if (_gpuSkinnedDraws.Count > 0)
                {
                    var boneSpan = CollectionsMarshal.AsSpan(_boneMatrices);
                    bool packedMainSlots = false;
                    for (int d = 0; d < _gpuSkinnedDraws.Count; d++)
                    {
                        var dr = _gpuSkinnedDraws[d];
                        if (!dr.VisibleMain) continue;
                        _model.PackSkinnedMainSlot(dr.Slot, dr.World, dr.Tint, dr.Emissive, dr.SpecParams,
                            boneSpan.Slice(dr.BoneSpanStart, dr.BoneCount));
                        // header (Mvp/Model/P) + the per-draw frame block + this mesh's bones = the palette-only upload.
                        _frameStats.AddSkinnedUniformUpload((long)(ModelRenderer.SkinnedBonesOffset + (uint)dr.BoneCount * 64));
                        packedMainSlots = true;
                    }
                    if (packedMainSlots) _model.UploadSkinnedMainSlots(cl);
                    bool dissolveBound = false;
                    _model.BindSkinnedPass(cl);
                    for (int d = 0; d < _gpuSkinnedDraws.Count; d++)
                    {
                        var dr = _gpuSkinnedDraws[d];
                        if (!dr.VisibleMain) continue;
                        if (dr.Dissolve != dissolveBound)   // switch pipelines only when the dissolve state changes
                        {
                            if (dr.Dissolve) _model.BindSkinnedDissolvePass(cl); else _model.BindSkinnedPass(cl);
                            dissolveBound = dr.Dissolve;
                        }
                        _model.DrawGpuSkinned(cl, dr.RestVb, dr.Ib, dr.IndexCount, dr.IndexFormat, dr.Slot, dr.SkinnedMaterialSet);
                        CountSkinnedDraw(dr.IndexCount);
                    }
                }
            }
            else if (_cpuSkinnedDraws.Count > 0)
            {
                // CPU path: the deformed geometry uploaded above, drawn through the rigid (no-bone) model pipeline.
                _model.BindPass(cl);   // re-bind the model pipeline (the skinned draws follow the rigid run)
                bool dissolveBound = false;
                for (int d = 0; d < _cpuSkinnedDraws.Count; d++)
                {
                    var dr = _cpuSkinnedDraws[d];
                    if (!dr.VisibleMain) continue;
                    if (dr.Dissolve != dissolveBound)   // switch pipelines only when the dissolve state changes
                    {
                        if (dr.Dissolve) _model.BindDissolvePass(cl); else _model.BindPass(cl);
                        dissolveBound = dr.Dissolve;
                    }
                    _model.DrawCpuSkinned(cl, dr.Ib, dr.IndexCount, dr.IndexFormat, dr.BaseVertex, (uint)d, dr.MaterialSet);
                    CountSkinnedDraw(dr.IndexCount);
                }
            }

            if (EnableTiming) modelMs = ElapsedMs(timingStart);
            timingStart = EnableTiming ? Stopwatch.GetTimestamp() : 0;

            // Textured billboards: drawn into the SAME model framebuffer (still bound), after the meshes, with the
            // depth test on (no write). This is what gives mesh/sprite depth interleaving; then the whole MRT
            // (meshes + textured billboards) goes through the post chain together.
            DrawTexturedBillboards(cl);

            // Beams: same model FB (still bound), after the textured billboards, before the post chain - so they
            // depth-interleave with the meshes and go through the pixel post like everything else in the model pass.
            DrawBeams(cl);

            // Trails: same model FB (still bound), right after the beams - depth-interleaved with the meshes and
            // through the pixel post like the rest of the model pass.
            DrawTrails(cl);

            // Overlay meshes (collision proxies etc.): after the model pass wrote depth (meshes + textured billboards
            // + beams), draw the queued translucent unlit proxies into the SAME model FB with the depth test on (no
            // write), so a proxy is occluded by nearer geometry yet blends over farther geometry, then flows through
            // the post chain with the rest of the model pass. Fully skipped when nothing is queued, so a frame with no
            // overlay draws renders byte-identical to before this pass existed.
            if (_overlayMeshDraws.Count > 0)
            {
                cl.SetFramebuffer(_res.ModelFB);
                int on = _overlayMeshDraws.Count;
                _overlayMeshes.EnsureCapacity(on);
                _overlayMeshes.BeginFrame(GpuClip.Correct(vp, _gd.Capabilities));
                // Sort the overlay proxies back-to-front by their world-origin view depth: they alpha-blend with
                // depth-write off, so overlapping proxies must composite far-to-near (the pre-sort submission order
                // blended wrong when a near proxy was queued before a far one behind it). Uses each draw's own UBO
                // slot indexed by the sorted position k, so the slot assignment stays unique.
                _sortCenters.Clear();
                for (int i = 0; i < on; i++) _sortCenters.Add(_overlayMeshDraws[i].World.Translation);
                TransparencySort.ComputeOrder(CollectionsMarshal.AsSpan(_sortCenters), on,
                    ActiveCamera.Eye, ActiveCamera.Forward, ref _sortKeys, ref _sortOrder);
                for (int k = 0; k < on; k++)
                {
                    var (handle, world) = _overlayMeshDraws[_sortOrder[k]];
                    if (!_slots.IsValid(handle.Index, handle.Generation)) continue;   // stale handle: skip
                    var m = _meshes[handle.Index];
                    if (m is not { } mesh) continue;
                    _overlayMeshes.Draw(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, k, ToRender(world));
                    _frameStats.DrawCalls++;
                }
            }

            // Under MSAA the geometry passes wrote a MULTISAMPLED MRT, so resolve the depth AND the encoded normal into
            // the single-sample DepthColorTex / NormalTex now - before the decals, which SAMPLE both (the depth to
            // reconstruct the surface, the normal so a void-fallback decal can tell a terrain dip from the top of a
            // cliff face), and before the post edge pass which also samples them. Every normal writer binds ModelFB
            // and is complete by this line. Nothing after it does. No-op when not multisampled.
            _res.ResolveDepthNormal(cl);

            // Background pass, before the decals: whichever mode is selected paints the no-geometry pixels and marks
            // them alpha 1. Mutually exclusive by construction (Post.Background derives the sky-over-starfield
            // precedence), so at most one of these runs, and Solid runs neither. The far-plane sky triangle passes
            // the Equal read-only depth test ONLY where the stored depth still EQUALS the cleared far plane
            // (background where no mesh drew), so it fills the gradient + sun there and geometry pixels (depth < 1)
            // reject it. Both passes write only the colour attachment (never the MRT normal/linear-depth the outline
            // pass reads) with alpha 1, marking those pixels as opaque painted background. Fully skipped when
            // Solid, so a Solid frame renders byte-identical to before this pass existed.
            switch (Post.Background)
            {
                case BackgroundMode.Sky:
                    _sky.Draw(cl, _res, ActiveCamera.View, ActiveCamera.Projection, Post.LightDirection, Post.Sky);
                    _frameStats.DrawCalls++;
                    break;
                case BackgroundMode.Starfield:
                    _starfield.Draw(cl, _res, Post.BackgroundColor);
                    _frameStats.DrawCalls++;
                    break;
            }

            // Ground decals: after the model pass wrote depth (meshes + textured billboards + beams), paint the
            // queued decals onto the reconstructed surface into ColorTex, BEFORE post - so they conform to the
            // ground, are occluded by geometry (Y-band), and flow through the pixel post like the meshes.
            if (_decals.Count > 0)
                // Batched decal pass: one instanced draw per blend run (see GroundDecalRenderer), so add the run count.
                // rejectDynamicGeometry: true. This is the main pass, after ResolveDepthNormal, so the normal target
                // carries the model pass's dynamic tags - reject skinned-tagged pixels so decals stay off characters (#235).
                _frameStats.DrawCalls += _decalRenderer.Draw(cl, _res, vp, EffectTimeSeconds, DecalQuality, Post.Hdr.Enabled, true, RelativeDecals(_decals));

            // Animated water (Rendering gap #5): after the sky + ground decals, sampling the resolved scene depth
            // (already valid via the ResolveDepthNormal call above the sky pass) for the shore fade. Depth test ON (so
            // terrain/props above the surface occlude it) but depth WRITE off (so it never touches the normal/
            // linear-depth MRT the outline pass reads below) - see Rendering.WaterRenderer / ShaderSources.WaterVert
            // for the full reasoning. Fully skipped when nothing is queued, so a frame with no DrawWater call
            // renders byte-identical to before this pass existed.
            if (_waterPlanes.Count > 0)
            {
                _water.Draw(cl, _res, RelativeWaterPlanes(), vp,
                    Post.LightDirection, Post.LightColor, eye, Post.Water, Post.Sky, EffectTimeSeconds, _frameOrigin);
                _frameStats.DrawCalls++;
                // The ocean FFT's remaining GPU drain (the one a frame pays to PRIME its cascades, since #398
                // ping-ponged the per-frame one away) is a Submit+WaitForIdle stall, not draw-call encode cost, so
                // it gets its own WaterSyncMs bucket (#374) rather than being misattributed to transparents. It no
                // longer lands INSIDE this bracket: since #423 the prime runs in PrepareFrame, before the frame's
                // list is even open, so there is nothing to carve out of transparentsMs - only to report. The water
                // renderer times that exact span with its own Stopwatch (OceanFftProducer.LastStallMs).
                if (EnableTiming) waterSyncMs = (float)_water.LastOceanCost.StallMs;
            }

            // Modern particle sprites: after the sky + decals + water (so effects composite over water surfaces),
            // before the post chain, into ColorDepthFB with the depth test on (no write) so geometry occludes
            // them. ONE premultiplied instanced draw over the back-to-front sorted queue: alpha smoke and additive
            // glow interleave correctly, and additive energy feeds bloom. The fragment samples the resolved
            // DepthColorTex (valid via the ResolveDepthNormal call above the sky pass) for the soft fade, skipping
            // samples that equal the background clear marker (Post.BackgroundColor red channel) so sprites never
            // dim against empty sky. Fully skipped when nothing is queued, so a frame with no DrawParticle call
            // renders byte-identical to before this pass existed.
            if (_particleSprites.Count > 0)
            {
                BuildSortedParticles();
                BillboardGeometry.CameraBasis(ActiveCamera.Forward, out Vector3 pRight, out Vector3 pUp);
                _particleTexResolver ??= ResolveTextureByListIndex;
                _frameStats.DrawCalls += _particleRenderer.Draw(cl, _res, vp, eye, pRight, pUp,
                    EffectTimeSeconds, ParticleSoftFade, ParticleQuality, Post.BackgroundColor.R,
                    CollectionsMarshal.AsSpan(_particleSorted), _particleTexResolver);
                _frameStats.Instances += _particleSorted.Count;
            }

            // Depth-tested debug wire volumes (DebugDepthMode.DepthTested): drawn into ColorDepthFB after the sky +
            // decals + water, which still holds the opaque meshes' depth, so terrain/props occlude the buried parts.
            // Depth test less-or-equal, NO depth write (never touches the MRT normal/linear-depth the outline pass
            // reads). Alpha blend. Drawing AFTER the sky pass (not with the beams before it) is deliberate: the sky
            // fills every far-plane background pixel, and these lines don't write depth, so an earlier draw would be
            // painted over where a volume rises above the horizon. Flows through the post chain like the model pass.
            // The always-on-top variant instead feeds the post-pass line overlay below (_lineVerts). No-op when empty.
            if (_depthLineVerts.Count > 0)
            {
                _depthLines.Draw(cl, vp, CollectionsMarshal.AsSpan(_depthLineVerts), _res.ColorDepthFB);
                _frameStats.DrawCalls++;
            }

            // Resolve the multisampled lit COLOUR into the single-sample target the post chain samples (after all
            // colour writers: geometry + decals + water). The encoded normal is NOT resolved here: it lands earlier,
            // with the depth, because the ground-decal pass reads it. No-op when not multisampled.
            _res.ResolveColor(cl);
            // ColorTex now holds this frame's resolved colour: mark it so next frame's crossfade capture knows a real
            // previous frame exists (guards against sampling a blank target the frame right after a resize).
            _transitions.NoteFrameResolved();

            // Screen-space distortion offset field: accumulate the queued distortion sprites into the (lazily
            // allocated above) half/quarter-res target, reading the resolved scene depth (via the ResolveDepthNormal above
            // the sky pass) for occlusion, BEFORE the post chain re-samples the scene colour through it (the apply
            // pass is _post.Run's FIRST pass). Same camera basis + background marker as the particle pass. resRatio
            // (the full-res/offset-res divisor) matches EnsureDistortion's divisor so the fragment scales its
            // half/quarter-res gl_FragCoord to the full-res depth texel. Fully skipped when nothing is queued.
            if (distortionActive && _res.DistortAllocated)
            {
                BillboardGeometry.CameraBasis(ActiveCamera.Forward, out Vector3 dRight, out Vector3 dUp);
                float resRatio = DistortionQuality == DistortionQuality.Full ? 2f : 4f;
                _frameStats.DrawCalls += _distortionRenderer.Draw(cl, _res, vp, eye, dRight, dUp,
                    EffectTimeSeconds, ParticleSoftFade, DistortionQuality, Post.BackgroundColor.R, resRatio,
                    RelativeDistortionSprites());
            }

            // Pure encode cost now: the ocean-FFT prime stall this bracket used to swallow (issue #374) is paid in
            // PrepareFrame, outside the frame's list and outside this bracket, so there is nothing to subtract.
            // WaterSyncMs still reports it, from the same measured span (#423).
            if (EnableTiming) transparentsMs += ElapsedMs(timingStart);
            timingStart = EnableTiming ? Stopwatch.GetTimestamp() : 0;
            _post.Run(cl, _res, target, Post, runFxaa, distortionActive);
            if (EnableTiming) postMs = ElapsedMs(timingStart);
            timingStart = EnableTiming ? Stopwatch.GetTimestamp() : 0;

            // Filled overlay: rebind `target` and draw the accumulated translucent triangles on top of the post
            // image, BEFORE the lines so an outline drawn on top of a fill reads crisp. Depth disabled + alpha
            // blend; same ActiveCamera.ViewProjection as the model pass (so fills line up with geometry and picking).
            if (_fillVerts.Count > 0)
            {
                _fills.Draw(cl, vp, CollectionsMarshal.AsSpan(_fillVerts), target);
                _frameStats.DrawCalls++;
            }

            // Debug overlay: rebind `target` and draw the accumulated lines on top of the post image, with
            // depth disabled and alpha blend. ActiveCamera.ViewProjection matches the model pass (unflipped, so
            // lines line up with rendered geometry and with ScreenToGround picking).
            if (_lineVerts.Count > 0)
            {
                _lines.Draw(cl, vp, CollectionsMarshal.AsSpan(_lineVerts), target);
                _frameStats.DrawCalls++;
            }

            // Billboards: after the line pass, additive first (glow) then alpha, same overlay framebuffer +
            // ViewProjection. Each rebinds `target` (no clear) and uploads its own vertex span. Additive is
            // order-independent (kept in submission order); the alpha stream is built back-to-front so overlapping
            // translucent billboards composite far-to-near regardless of the order the host queued them.
            if (_billboardAdditive.Count > 0)
            {
                _billboards.Draw(cl, vp, CollectionsMarshal.AsSpan(_billboardAdditive), target, additive: true);
                _frameStats.DrawCalls++;
            }
            BuildSortedAlphaBillboards();
            if (_billboardAlpha.Count > 0)
            {
                _billboards.Draw(cl, vp, CollectionsMarshal.AsSpan(_billboardAlpha), target, additive: false);
                _frameStats.DrawCalls++;
            }

            // Screen-space teleport transition (blink / frozen-frame crossfade): a fullscreen pass over everything,
            // last so it masks the whole 3D frame. No-op (and byte-identical) when none is active.
            _transitions.Render(cl, _res, target, ScreenTransition);

            if (EnableTiming)
            {
                transparentsMs += ElapsedMs(timingStart);
                _passTimingsMs = new Scene3DPassTimingsMs(shadowDepthMs, modelMs, transparentsMs, waterSyncMs, postMs);
            }
        }

        /// <summary>Elapsed milliseconds since <paramref name="startTimestamp"/> (a <see cref="Stopwatch.GetTimestamp"/>
        /// snapshot). Pulled out so every timing bracket in <see cref="RenderInternal"/> shares one conversion.</summary>
        static float ElapsedMs(long startTimestamp) =>
            (float)(Stopwatch.GetTimestamp() - startTimestamp) * 1000f / Stopwatch.Frequency;

        // Frame-counter helpers (LastFrameStats). One instanced mesh/splat/shadow-run draw: a draw call carrying
        // instanceCount instances of an indexCount-index mesh (indexCount/3 triangles each).
        void CountMeshDraw(int indexCount, uint instanceCount)
        {
            _frameStats.DrawCalls++;
            _frameStats.Instances += (int)instanceCount;
            _frameStats.Triangles += (long)(indexCount / 3) * instanceCount;
        }

        // One CPU-skinned (or skinned shadow-caster) draw: a single-instance indexed draw.
        void CountSkinnedDraw(int indexCount)
        {
            _frameStats.DrawCalls++;
            _frameStats.Instances++;
            _frameStats.Triangles += indexCount / 3;
        }

        /// <summary>Rebuild <see cref="_particleSorted"/> from the queued particle sprites in BACK-TO-FRONT order
        /// by view depth (reusing the shared sort scratch buffers, no per-frame allocation). The whole stream is
        /// sorted regardless of blend: premultiplied compositing makes the additive sprites order-independent and
        /// the alpha sprites order-correct in one draw. Ties keep submission order (stable sort).</summary>
        void BuildSortedParticles()
        {
            _particleSorted.Clear();
            int n = _particleSprites.Count;
            if (n == 0) return;
            _sortCenters.Clear();
            for (int i = 0; i < n; i++) _sortCenters.Add(_particleSprites[i].Position);
            TransparencySort.ComputeOrder(CollectionsMarshal.AsSpan(_sortCenters), n,
                ActiveCamera.Eye, ActiveCamera.Forward, ref _sortKeys, ref _sortOrder);
            for (int k = 0; k < n; k++) _particleSorted.Add(ToRender(_particleSprites[_sortOrder[k]]));   // reduced on the copy, after the sort
        }

        /// <summary>Expand the queued alpha billboards into <see cref="_billboardAlpha"/> in BACK-TO-FRONT order:
        /// compute each item's view depth along the camera forward axis, sort the indices far-to-near, then build
        /// the 6-vertex quad for each in that order. Reuses the frame's billboard camera basis and the shared sort
        /// buffers, so no per-frame LINQ/comparer allocation. No-op when nothing is queued.</summary>
        void BuildSortedAlphaBillboards()
        {
            _billboardAlpha.Clear();
            int n = _billboardAlphaItems.Count;
            if (n == 0) return;

            if (!_billboardBasisValid)
            {
                BillboardGeometry.CameraBasis(ActiveCamera.Forward, out _billboardRight, out _billboardUp);
                _billboardBasisValid = true;
            }

            _sortCenters.Clear();
            for (int i = 0; i < n; i++) _sortCenters.Add(_billboardAlphaItems[i].Center);
            TransparencySort.ComputeOrder(CollectionsMarshal.AsSpan(_sortCenters), n,
                ActiveCamera.Eye, ActiveCamera.Forward, ref _sortKeys, ref _sortOrder);

            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            for (int k = 0; k < n; k++)
            {
                var it = _billboardAlphaItems[_sortOrder[k]];
                BillboardGeometry.Triangles(ToRender(it.Center), it.Size, _billboardRight, _billboardUp, pos, uv);
                for (int v = 0; v < 6; v++)
                    _billboardAlpha.Add(new BillboardRenderer.BillboardVertex(pos[v], uv[v], it.Color));
            }
        }

        /// <summary>Sort the queued textured billboards back-to-front by view depth, coalesce the sorted stream into
        /// same-(texture,blend) runs, then draw each run into the model framebuffer. The model FB is still bound from
        /// the mesh pass; the depth buffer holds the meshes' depth so the quads interleave. Sorting composites
        /// overlapping alpha quads far-to-near; additive quads are order-independent, so mixing them into the same
        /// depth order is harmless and keeps an additive quad behind an alpha one correctly under it. No-op when
        /// nothing is queued.</summary>
        void DrawTexturedBillboards(IGpuCommandList cl)
        {
            if (_texBillboardItems.Count == 0) return;

            SortTexturedBillboardsBackToFront();
            CoalesceTexturedBillboards(_texBillboardSorted, _texBillboardRuns);

            // Camera basis is constant across the frame; compute once and reuse for every quad.
            BillboardGeometry.CameraBasis(ActiveCamera.Forward, out Vector3 right, out Vector3 up);
            _texBillboards.SetViewProj(cl, FrameViewProjection());

            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            foreach (var run in _texBillboardRuns)
            {
                _texBillboardVerts.Clear();
                for (int i = run.Start; i < run.Start + run.Count; i++)
                {
                    var it = _texBillboardSorted[i];
                    BillboardGeometry.Triangles(ToRender(it.Center), it.Size, right, up, it.SourceUv, pos, uv);
                    for (int v = 0; v < 6; v++)
                        _texBillboardVerts.Add(new BillboardRenderer.BillboardVertex(pos[v], uv[v], it.Color));
                }
                IGpuResourceSet set = GetTexBillboardSet(run.TexIndex);
                _texBillboards.Draw(cl, CollectionsMarshal.AsSpan(_texBillboardVerts), _res.ModelFB, set,
                    run.Blend == BillboardBlend.Additive);
                _frameStats.DrawCalls++;
            }
        }

        /// <summary>Reorder <see cref="_texBillboardItems"/> into <see cref="_texBillboardSorted"/> back-to-front by
        /// view depth (a stable sort, so equal-depth quads keep submission order). Reuses the shared sort buffers, so
        /// no per-frame allocation. Called before coalescing so a run boundary falls on the sorted order.</summary>
        void SortTexturedBillboardsBackToFront() =>
            SortTexturedBillboardsBackToFront(_texBillboardItems, ActiveCamera.Eye, ActiveCamera.Forward,
                _sortCenters, ref _sortKeys, ref _sortOrder, _texBillboardSorted);

        /// <summary>
        /// Pure (GPU-free) back-to-front reorder used by the textured-billboard pass: fill <paramref name="sorted"/>
        /// with <paramref name="items"/> reordered farthest-first by the view depth of each item's <c>Center</c>
        /// along the camera (<paramref name="eye"/>, <paramref name="forward"/>), stable for equal depths. The three
        /// scratch/output buffers are caller-owned and reused (Cleared + refilled, geometric growth), so a
        /// steady-state frame does not allocate. Headless-testable so the reorder-before-upload decision is proven
        /// without a device.
        /// </summary>
        internal static void SortTexturedBillboardsBackToFront(IReadOnlyList<TexturedBillboardItem> items,
            Vector3 eye, Vector3 forward, List<Vector3> centersScratch, ref float[] keyScratch, ref int[] order,
            List<TexturedBillboardItem> sorted)
        {
            int n = items.Count;
            centersScratch.Clear();
            for (int i = 0; i < n; i++) centersScratch.Add(items[i].Center);
            TransparencySort.ComputeOrder(CollectionsMarshal.AsSpan(centersScratch), n, eye, forward,
                ref keyScratch, ref order);
            sorted.Clear();
            for (int k = 0; k < n; k++) sorted.Add(items[order[k]]);
        }

        /// <summary>Build each queued beam's camera-facing strip (via <see cref="BeamGeometry"/>) into one vertex
        /// stream and draw them all in a single additive pass into the model FB. The model FB is still bound from
        /// the mesh pass; its depth buffer holds the meshes' depth so the beams interleave. No-op when nothing is
        /// queued.</summary>
        void DrawBeams(IGpuCommandList cl)
        {
            if (_beamItems.Count == 0) return;

            Vector3 viewDir = ActiveCamera.Forward;   // constant across the frame, matching the billboard basis
            _beamVerts.Clear();
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            foreach (var it in _beamItems)
            {
                int n = BeamGeometry.Triangles(ToRender(it.A), ToRender(it.B), viewDir, it.Width, pos, uv);
                for (int v = 0; v < n; v++)
                    _beamVerts.Add(new BeamRenderer.BeamVertex(pos[v], uv[v], it.CoreColor, it.GlowColor, it.Shape, it.Anim));
            }
            if (_beamVerts.Count == 0) return;

            _beams.SetFrameUniforms(cl, FrameViewProjection(), EffectTimeSeconds);
            _beams.Draw(cl, CollectionsMarshal.AsSpan(_beamVerts), _res.ModelFB);
            _frameStats.DrawCalls++;
        }

        /// <summary>Build each queued trail's mitered strip (via <see cref="TrailGeometry"/>) into per-blend vertex
        /// streams and draw them into the model FB. Style (tint * per-vertex alpha, soft-edge) is folded into each
        /// vertex here so <see cref="TrailGeometry"/> stays pure. Additive first, then alpha. No-op when nothing is
        /// queued.</summary>
        void DrawTrails(IGpuCommandList cl)
        {
            if (_trailItems.Count == 0) return;

            Vector3 viewDir = ActiveCamera.Forward;   // constant across the frame, matching the beam/billboard basis
            _trailVertsAdditive.Clear();
            _trailVertsAlpha.Clear();

            var allSamples = CollectionsMarshal.AsSpan(_trailSamples);
            foreach (var it in _trailItems)
            {
                _trailScratchPos.Clear();
                _trailScratchUv.Clear();
                _trailScratchAlpha.Clear();
                var span = RelativeTrailSamples(allSamples.Slice(it.Start, it.Count));
                int nv = TrailGeometry.Build(span, viewDir, _trailScratchPos, _trailScratchUv, _trailScratchAlpha);
                if (nv == 0) continue;

                var dst = it.Style.Blend == TrailBlend.Alpha ? _trailVertsAlpha : _trailVertsAdditive;
                Vector4 col = it.Style.Color;
                float soft = it.Style.SoftEdge;
                for (int v = 0; v < nv; v++)
                {
                    Vector3 p = _trailScratchPos[v];
                    Vector2 uv = _trailScratchUv[v];
                    float a = _trailScratchAlpha[v];
                    dst.Add(new TrailRenderer.TrailVertex(
                        p, new Vector3(uv.X, uv.Y, soft), new Vector4(col.X, col.Y, col.Z, col.W * a)));
                }
            }

            if (_trailVertsAdditive.Count == 0 && _trailVertsAlpha.Count == 0) return;

            _trails.SetFrameUniforms(cl, FrameViewProjection());
            if (_trailVertsAdditive.Count > 0)
            {
                _trails.Draw(cl, CollectionsMarshal.AsSpan(_trailVertsAdditive), _res.ModelFB, TrailBlend.Additive);
                _frameStats.DrawCalls++;
            }
            if (_trailVertsAlpha.Count > 0)
            {
                _trails.Draw(cl, CollectionsMarshal.AsSpan(_trailVertsAlpha), _res.ModelFB, TrailBlend.Alpha);
                _frameStats.DrawCalls++;
            }
        }

        /// <summary>Get (creating on first use) the textured-billboard resource set for the texture at
        /// <paramref name="texListIndex"/>. Cached parallel to <c>_textures</c>; disposed in <see cref="Dispose"/>.</summary>
        IGpuResourceSet GetTexBillboardSet(int texListIndex)
        {
            while (_texBillboardSets.Count <= texListIndex) _texBillboardSets.Add(null);
            var set = _texBillboardSets[texListIndex];
            if (set is null)
            {
                set = _texBillboards.CreateTextureSet(_textures[texListIndex]!);
                _texBillboardSets[texListIndex] = set;
            }
            return set;
        }

        public void Dispose()
        {
            // Drain in-flight GPU work before destroying anything: a test or streaming path may Dispose the
            // scene with uploads/draws still queued on the device's async submission thread (Mesa lavapipe
            // executes queued commands on its own thread and segfaults on destroyed resources).
            _gd.WaitForIdle();
            _retired.Dispose();    // flushes the retired tail (it would outlive the scene) and frees the fence barrier
            _model.Dispose();
            _post.Dispose();
            _lines.Dispose();
            _depthLines.Dispose();
            _fills.Dispose();
            _billboards.Dispose();
            _transitions.Dispose();
            _texBillboards.Dispose();
            _beams.Dispose();
            _trails.Dispose();
            _decalRenderer.Dispose();
            _particleRenderer.Dispose();
            _distortionRenderer.Dispose();
            _sky.Dispose();
            _starfield.Dispose();
            _water.Dispose();
            _overlayMeshes.Dispose();
            _res.Dispose();
            foreach (var m in _meshes)
                if (m is { } mesh) { mesh.Vb.Dispose(); mesh.Ib.Dispose(); mesh.MaterialSet?.Dispose(); }
            foreach (var m in _skinnedMeshes)
                if (m is { } e) { e.Vb.Dispose(); e.Ib.Dispose(); e.MaterialSet?.Dispose(); }
            foreach (var s in _texBillboardSets) s?.Dispose();
            _texBillboardSets.Clear();
            foreach (var t in _textures) t?.Dispose();
            _textures.Clear();
            foreach (var s in _splatMaterials) s?.Dispose();
            _splatMaterials.Clear();
        }

        readonly struct Mesh
        {
            public readonly IGpuBuffer Vb, Ib;
            public readonly int IndexCount;
            /// <summary>GPU index width of <see cref="Ib"/> (UInt16 for meshes up to 65,536 verts, else UInt32).</summary>
            public readonly GpuIndexFormat IndexFormat;
            /// <summary>Per-mesh material resource set (UBO + albedo + sampler), or null => the renderer's white
            /// default. The texture itself is owned in Scene3D's <c>_textures</c> list, not here, so a texture can
            /// be shared by several meshes; only the set is owned per mesh.</summary>
            public readonly IGpuResourceSet? MaterialSet;
            /// <summary>Index into Scene3D's splat-material list when this mesh draws through the splat pipeline, else
            /// -1 (the normal model pipeline). Splat meshes carry no per-mesh <see cref="MaterialSet"/> (the splat set
            /// is shared and owned by the scene), so unload frees only Vb/Ib.</summary>
            public readonly int SplatMaterial;
            /// <summary>Mesh-local bounds (AABB + bounding sphere) computed once at load from the vertex positions,
            /// for frustum culling. The renderer transforms these by the per-instance world matrix (no per-frame
            /// vertex scan).</summary>
            public readonly MeshBounds Bounds;
            /// <summary>Alpha-cutout threshold for this mesh's material (0 = OPAQUE, no clip). Packed into each of
            /// this mesh's per-instance <c>SpecParams.z</c> by <c>ApplyAlphaCutoffs</c> so the model fragment
            /// discards texels below it (MASK foliage renders as its silhouette). 0 keeps the render byte-identical.</summary>
            public readonly float AlphaCutoff;
            public Mesh(IGpuBuffer vb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, in MeshBounds bounds, IGpuResourceSet? materialSet = null, int splatMaterial = -1, float alphaCutoff = 0f)
            {
                Vb = vb; Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat; Bounds = bounds; MaterialSet = materialSet; SplatMaterial = splatMaterial; AlphaCutoff = alphaCutoff;
            }
        }

        /// <summary>A loaded splat-terrain material: the two 5-layer texture arrays (albedo, normal), the combined
        /// frame+params UBO (frame portion re-synced each frame), and the resource set. Owned by Scene3D; shared by
        /// every mesh that uses it.</summary>
        sealed class SplatMaterialEntry
        {
            public readonly IGpuTexture AlbedoArray, NormalArray;
            public readonly IGpuBuffer Ubo;
            public readonly IGpuResourceSet Set;
            readonly IGpuSampler? _ownedSampler;   // non-null only when the material overrode the shared sampler
            public SplatMaterialEntry(IGpuTexture albedo, IGpuTexture normal, IGpuBuffer ubo, IGpuResourceSet set, IGpuSampler? ownedSampler = null)
            { AlbedoArray = albedo; NormalArray = normal; Ubo = ubo; Set = set; _ownedSampler = ownedSampler; }
            public void Dispose() { Set.Dispose(); AlbedoArray.Dispose(); NormalArray.Dispose(); Ubo.Dispose(); _ownedSampler?.Dispose(); }
        }

        /// <summary>A GPU-resident skinned mesh: its vertex/index buffers, index count, optional material set, the
        /// CPU-side inverse-bind matrices needed to compose per-frame bone palettes at DrawSkinned time, and its
        /// rest-pose local <see cref="Bounds"/> (used to frustum-cull queued draws before the CPU skin pass -
        /// see <see cref="ClassifySkinnedVisibility"/>).</summary>
        sealed class SkinnedMeshEntry
        {
            public readonly IGpuBuffer Vb, Ib;
            public readonly int IndexCount;
            public readonly GpuIndexFormat IndexFormat;
            public readonly IGpuResourceSet? MaterialSet;          // set-0 CPU-path material (frame UBO vertex|fragment)
            public readonly IGpuResourceSet? SkinnedMaterialSet;   // set-1 GPU-skinning material (fragment-only frame UBO)
            public readonly Matrix4x4[] InverseBind;
            public readonly MeshBounds Bounds;
            public SkinnedMeshEntry(IGpuBuffer vb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat,
                IGpuResourceSet? materialSet, IGpuResourceSet? skinnedMaterialSet, Matrix4x4[] inverseBind, in MeshBounds bounds)
            {
                Vb = vb; Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat;
                MaterialSet = materialSet; SkinnedMaterialSet = skinnedMaterialSet; InverseBind = inverseBind; Bounds = bounds;
            }
        }

        /// <summary>One GPU-skinned draw (built per frame in RenderInternal when <see cref="UseGpuSkinning"/> is on).
        /// Carries the mesh's rest-pose vertex + index buffers (uploaded once at load - the GPU deforms them), the
        /// set-1 material set, the composed bone-palette slice (offset into <c>_boneMatrices</c> + bone count), the
        /// compacted combined-UBO slot, and the per-draw matrices/material the vertex shader folds. The shadow depth
        /// pass packs + draws every entry (out-of-volume ones clip away). The main pass skips a
        /// <see cref="VisibleMain"/>-false entry (camera-culled, kept only as a shadow caster).</summary>
        readonly struct GpuSkinnedDraw
        {
            public readonly IGpuBuffer RestVb, Ib;
            public readonly int IndexCount;
            public readonly GpuIndexFormat IndexFormat;
            public readonly IGpuResourceSet? SkinnedMaterialSet;
            public readonly int BoneSpanStart;   // into _boneMatrices (submission index * MaxBonesPerDraw)
            public readonly int BoneCount;
            public readonly uint Slot;            // compacted combined-UBO slot (main + shadow share it)
            public readonly Matrix4x4 World;
            public readonly Vector4 Tint, Emissive, SpecParams;
            public readonly bool VisibleMain;
            public readonly bool Dissolve;
            public GpuSkinnedDraw(IGpuBuffer restVb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat,
                IGpuResourceSet? skinnedMaterialSet, int boneSpanStart, int boneCount, uint slot,
                in Matrix4x4 world, Vector4 tint, Vector4 emissive, Vector4 specParams, bool visibleMain, bool dissolve)
            {
                RestVb = restVb; Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat;
                SkinnedMaterialSet = skinnedMaterialSet; BoneSpanStart = boneSpanStart; BoneCount = boneCount; Slot = slot;
                World = world; Tint = tint; Emissive = emissive; SpecParams = specParams; VisibleMain = visibleMain; Dissolve = dissolve;
            }
        }

        /// <summary>One CPU-skinned draw: the mesh's index buffer + count, the base vertex of its deformed verts in
        /// the shared skinned vertex stream, and its optional material set. Built per frame in RenderInternal.
        /// Every entry here was CPU-skinned and uploaded (needed by at least one of the main or shadow pass). The
        /// shadow depth pass draws every entry unconditionally (see RenderShadowDepthPass), while the main pass
        /// draw loop skips an entry whose <see cref="VisibleMain"/> is false (camera-culled, kept only because it
        /// is still a shadow caster).</summary>
        readonly struct CpuSkinnedDraw
        {
            public readonly IGpuBuffer Ib;
            public readonly int IndexCount;
            public readonly GpuIndexFormat IndexFormat;
            public readonly int BaseVertex;
            public readonly IGpuResourceSet? MaterialSet;
            public readonly bool Dissolve;   // route through the CharDissolve pipeline variant
            public readonly bool VisibleMain;   // draw in the main visible pass, always true when culling is off
            public CpuSkinnedDraw(IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, int baseVertex, IGpuResourceSet? materialSet, bool dissolve = false, bool visibleMain = true)
            {
                Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat; BaseVertex = baseVertex; MaterialSet = materialSet; Dissolve = dissolve; VisibleMain = visibleMain;
            }
        }

        /// <summary>A contiguous run of instances of one mesh handle inside the flat instance array.</summary>
        internal readonly struct MeshRun
        {
            public readonly MeshHandle Mesh;
            public readonly uint Start;
            public readonly uint Count;
            public MeshRun(MeshHandle mesh, uint start, uint count) { Mesh = mesh; Start = start; Count = count; }
            public MeshRun(int meshIndex, uint start, uint count) : this(new MeshHandle(meshIndex), start, count) { }
        }

        /// <summary>One queued colour-only alpha billboard (world centre + half-size + RGBA tint). Stored in
        /// submission order, then sorted back-to-front by view depth before the vertex stream is built. Additive
        /// billboards are not stored here (they bypass the sort - see <see cref="_billboardAdditive"/>).</summary>
        internal struct BillboardItem
        {
            public Vector3 Center;
            public float Size;
            public Vector4 Color;
        }

        /// <summary>One queued textured billboard (resolved texture list index + blend + transform + source rect +
        /// tint). Stored in submission order; coalesced into runs at render time.</summary>
        internal struct TexturedBillboardItem
        {
            public int TexIndex;          // ListIndex into _textures
            public BillboardBlend Blend;
            public Vector3 Center;
            public float Size;
            public Vector4 SourceUv;      // (u0,v0,u1,v1)
            public Vector4 Color;
        }

        /// <summary>A contiguous run of textured-billboard items sharing one texture + blend, drawn as one call.</summary>
        internal readonly struct TexturedBillboardRun
        {
            public readonly int TexIndex;
            public readonly BillboardBlend Blend;
            public readonly int Start;
            public readonly int Count;
            public TexturedBillboardRun(int texIndex, BillboardBlend blend, int start, int count)
            {
                TexIndex = texIndex; Blend = blend; Start = start; Count = count;
            }
        }

        /// <summary>One queued beam: world endpoints + width, resolved core/glow colours (RGBA as Vector4), and two
        /// packed param vectors (Shape: coreFrac/glowSoftness/taper; Anim: pulseSpeed/pulseAmount/scrollSpeed).
        /// Built in <see cref="DrawBeam"/>; consumed in <see cref="DrawBeams"/>.</summary>
        internal struct BeamItem
        {
            public Vector3 A, B;
            public float Width;
            public Vector4 CoreColor;
            public Vector4 GlowColor;
            public Vector4 Shape;
            public Vector4 Anim;
        }

        /// <summary>One queued trail: a span (<see cref="Start"/>/<see cref="Count"/>) into the frame's flat
        /// <c>_trailSamples</c> pool plus its <see cref="Style"/>. Built in <see cref="DrawTrail"/>; consumed in
        /// <see cref="DrawTrails"/>.</summary>
        internal struct TrailItem
        {
            public int Start;
            public int Count;
            public TrailStyle Style;
        }

        /// <summary>
        /// Coalesce <paramref name="items"/> (already in draw order - the caller sorts them back-to-front first)
        /// into <paramref name="runs"/>: each run is a maximal span of consecutive items sharing the same texture
        /// index AND blend. Item order is preserved (a texture/blend change starts a new run rather than merging
        /// non-adjacent items), so the back-to-front order the caller established survives coalescing. Pure +
        /// headless-testable; <paramref name="runs"/> is Cleared and refilled.
        /// </summary>
        internal static void CoalesceTexturedBillboards(IReadOnlyList<TexturedBillboardItem> items, List<TexturedBillboardRun> runs)
        {
            runs.Clear();
            if (items.Count == 0) return;

            int start = 0;
            for (int i = 1; i <= items.Count; i++)
            {
                bool boundary = i == items.Count
                    || items[i].TexIndex != items[start].TexIndex
                    || items[i].Blend != items[start].Blend;
                if (boundary)
                {
                    runs.Add(new TexturedBillboardRun(items[start].TexIndex, items[start].Blend, start, i - start));
                    start = i;
                }
            }
        }

        /// <summary>Compose <paramref name="boneMatrices"/> (per-frame joint world transforms) with
        /// <paramref name="inverseBind"/> and write them into <paramref name="dst"/> at bone slot
        /// <paramref name="slot"/> (matrix index <c>slot * MaxBonesPerDraw</c>). <paramref name="dst"/> is grown to
        /// hold the whole slot and any gap is identity-filled, so each draw's dynamic-offset window reads only its
        /// own (and harmless identity) matrices. Pure + headless-testable. Throws if the two inputs differ in length
        /// or the mesh exceeds the per-draw bone cap.</summary>
        internal static void ComposeBonesIntoSlot(List<Matrix4x4> dst, int slot,
            ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4[] inverseBind)
        {
            if (boneMatrices.Length != inverseBind.Length)
                throw new ArgumentException(
                    $"boneMatrices length {boneMatrices.Length} must equal the mesh bone count {inverseBind.Length}.");
            int cap = SkinningMath.MaxBonesPerDraw;
            if (boneMatrices.Length > cap)
                throw new ArgumentException($"a skinned mesh has {boneMatrices.Length} bones, over the {cap}-bone per-draw cap.");
            int need = (slot + 1) * cap;
            while (dst.Count < need) dst.Add(Matrix4x4.Identity);   // pad up to and including this slot (identity = no deform)
            int baseIdx = slot * cap;
            for (int b = 0; b < boneMatrices.Length; b++)
                dst[baseIdx + b] = SkinningMath.Compose(boneMatrices[b], inverseBind[b]);
            for (int b = boneMatrices.Length; b < cap; b++)
                dst[baseIdx + b] = Matrix4x4.Identity;             // clear the rest of the slot (reused list)
        }

        /// <summary>
        /// Radius safety factor applied to a skinned draw's REST-POSE bounding sphere before culling it (see
        /// <see cref="ClassifySkinnedVisibility"/>). A pose can carry vertices outside the rest-pose box - a
        /// swung limb, a jump - so culling against the raw rest bounds risks dropping a character whose silhouette
        /// has animated into view. 1.5x is a generous heuristic margin for a whole-body bounding sphere (a limb's
        /// swing is bounded by twice its own length, itself a fraction of the full-body radius). A rig with more
        /// extreme excursion (e.g. a long weapon swing far outside the body) should widen this or maintain its own
        /// per-pose bounds instead.
        /// </summary>
        internal const float SkinnedCullSafetyFactor = 1.5f;

        /// <summary>
        /// Conservative main-pass / shadow-caster visibility split for one queued skinned draw's rest-pose
        /// <paramref name="restBounds"/> transformed by its <paramref name="world"/> matrix, inflated by
        /// <see cref="SkinnedCullSafetyFactor"/>. <paramref name="cullMain"/> is <see cref="FrustumCulling"/> (off
        /// = always visible in the main pass, the rigid-instance parity path). <paramref name="shadowActive"/> is
        /// whether the shadow-map tier is resolved this frame (off = never a shadow caster). The caster is a shadow
        /// caster when its inflated sphere intersects ANY cascade's ortho volume in <paramref name="shadowFrustums"/>:
        /// under the frustum-slice fit the cascades no longer nest, so the union of all cascades is what keeps a
        /// caster inside a near cascade but outside the far one alive for the depth pass. Returns
        /// (VisibleMain, VisibleShadow) - a draw needs CPU skinning + upload iff either is true. Pure
        /// <see cref="MeshBounds"/> + <see cref="FrustumPlanes"/> arithmetic (both already unit-tested), no GPU,
        /// headless-testable.
        /// </summary>
        internal static (bool VisibleMain, bool VisibleShadow) ClassifySkinnedVisibility(
            in MeshBounds restBounds, in Matrix4x4 world,
            bool cullMain, in FrustumPlanes mainFrustum,
            bool shadowActive, ReadOnlySpan<FrustumPlanes> shadowFrustums)
        {
            if (!cullMain && !shadowActive) return (true, false);
            restBounds.WorldSphere(world, out Vector3 center, out float radius);
            float r = radius * SkinnedCullSafetyFactor;
            bool visibleMain = !cullMain || mainFrustum.IntersectsSphere(center, r);
            bool visibleShadow = false;
            if (shadowActive)
                for (int i = 0; i < shadowFrustums.Length && !visibleShadow; i++)
                    visibleShadow = shadowFrustums[i].IntersectsSphere(center, r);
            return (visibleMain, visibleShadow);
        }

        /// <summary>
        /// Fill <see cref="_instanceVisible"/> for this frame's grouped instance buffer: true where the instance's
        /// world-space bounding sphere is (conservatively) inside <paramref name="frustum"/>. When
        /// <see cref="FrustumCulling"/> is off every slot is visible (parity path). Also updates
        /// <see cref="_drawnInstances"/> / <see cref="_culledInstances"/>. Allocation-free on the hot path (the mask
        /// grows, never per-frame allocated). The shadow depth pass does not consult this mask.
        /// </summary>
        void ComputeMainPassVisibility(in FrustumPlanes frustum)
        {
            int total = _instanceData.Count;
            if (_instanceVisible.Length < total)
                _instanceVisible = new bool[Math.Max(total, _instanceVisible.Length * 2)];

            _drawnInstances = 0;
            _culledInstances = 0;
            if (total == 0) return;

            if (!FrustumCulling)
            {
                for (int i = 0; i < total; i++) _instanceVisible[i] = true;
                _drawnInstances = total;
                return;
            }

            // Walk runs so each slot's mesh bounds come from its run's mesh; the world matrix is the uploaded
            // instance model matrix. A stale-handle run (mesh unloaded this frame) is conservatively kept visible
            // (the draw loop skips it anyway by the same stale check), so culling never diverges from the draw.
            foreach (var run in _runs)
            {
                bool valid = _slots.IsValid(run.Mesh.Index, run.Mesh.Generation);
                Mesh mesh = default; bool haveMesh = false;
                if (valid && _meshes[run.Mesh.Index] is { } m) { mesh = m; haveMesh = true; }
                // Terrain (splat) chunks draw chunk-local under a PURE TRANSLATION (their region origin), so their
                // local AABB offset by that translation IS the world AABB: cull them with the tighter AABB test (a
                // flat chunk's bounding sphere is far too conservative), and the offset is exact. Props/models use
                // the world-sphere test (cheap under arbitrary scale/rotation). A splat instance under a rotation or
                // a scale (not produced by the terrain path) falls back to the sphere test.
                bool splatPlaced = haveMesh && mesh.SplatMaterial >= 0;
                for (uint s = 0; s < run.Count; s++)
                {
                    int slot = (int)(run.Start + s);
                    bool visible = true;
                    if (haveMesh)
                    {
                        Matrix4x4 world = _instanceData[slot].Model;
                        if (splatPlaced && IsPureTranslation(world, out Vector3 t))
                            visible = frustum.IntersectsAabb(mesh.Bounds.Min + t, mesh.Bounds.Max + t);
                        else
                        {
                            mesh.Bounds.WorldSphere(world, out Vector3 c, out float r);
                            visible = frustum.IntersectsSphere(c, r);
                        }
                    }
                    _instanceVisible[slot] = visible;
                    if (visible) _drawnInstances++; else _culledInstances++;
                }
            }
        }

        /// <summary>
        /// Group queued <paramref name="items"/> by mesh handle into <paramref name="instanceData"/> (a flat array
        /// ordered so all instances of one mesh are contiguous) and <paramref name="runs"/> (one
        /// <see cref="MeshRun"/> per unique mesh handle, in first-seen order). Pure + headless-testable; both output
        /// lists are Cleared and refilled (no realloc on the caller's reused buffers). <paramref name="meshRunIndex"/>
        /// is scratch (mesh handle -&gt; run index): pass the caller's reused dictionary to keep the whole grouping
        /// pass allocation-free and O(instances) (a dictionary lookup instead of a linear scan of the runs seen so
        /// far). Omit it (the default) for a one-off/test call, which allocates a local scratch dictionary instead.
        /// <paramref name="castKinds"/> (optional) receives each SLOT's shadow-caster classification, index-aligned
        /// to <paramref name="instanceData"/>: this is the one place that still knows which queued instance a slot
        /// came from, so the opt-out flag is read here (see Scene3D.ShadowCasters.cs). Omit it and no classification
        /// is produced, which the depth pass reads as "every caster opaque" (the pre-policy shape).
        /// </summary>
        internal static void GroupInstances(IReadOnlyList<SceneInstances.Instance> items,
            List<ModelRenderer.InstanceData> instanceData, List<MeshRun> runs,
            Dictionary<(int Index, int Generation), int>? meshRunIndex = null,
            List<ShadowCastKind>? castKinds = null)
        {
            instanceData.Clear();
            runs.Clear();
            castKinds?.Clear();
            if (items.Count == 0) return;

            meshRunIndex ??= new Dictionary<(int, int), int>();
            meshRunIndex.Clear();

            // First-seen mesh order. Instances are usually already mesh-coherent (one mesh per kind), so the run
            // list stays short. Pass 1: collect distinct mesh handles in first-seen order + count per mesh, O(1)
            // amortized per instance via meshRunIndex (a per-instance linear scan of the runs list so far would be
            // O(instances x uniqueMeshes), the hot path this dictionary replaces).
            for (int i = 0; i < items.Count; i++)
            {
                MeshHandle mesh = items[i].Mesh;
                var key = (mesh.Index, mesh.Generation);
                if (meshRunIndex.TryGetValue(key, out int slot))
                    runs[slot] = new MeshRun(mesh, 0, runs[slot].Count + 1);
                else
                {
                    meshRunIndex[key] = runs.Count;
                    runs.Add(new MeshRun(mesh, 0, 1));
                }
            }

            // Assign each run a start offset (prefix sum), and record per-mesh write cursors.
            // runs currently holds (meshIndex, 0, count) in first-seen order.
            uint cursor = 0;
            Span<uint> writeCursor = runs.Count <= 64 ? stackalloc uint[runs.Count] : new uint[runs.Count];
            for (int r = 0; r < runs.Count; r++)
            {
                uint start = cursor;
                writeCursor[r] = start;
                cursor += runs[r].Count;
                runs[r] = new MeshRun(runs[r].Mesh, start, runs[r].Count);
            }

            // Size the flat array, then scatter each instance into its mesh's contiguous slot, again via the same
            // O(1) map lookup instead of a linear run scan.
            int total = (int)cursor;
            for (int i = 0; i < total; i++) instanceData.Add(default);
            if (castKinds != null) for (int i = 0; i < total; i++) castKinds.Add(ShadowCastKind.Opaque);
            for (int i = 0; i < items.Count; i++)
            {
                var inst = items[i];
                MeshHandle mesh = inst.Mesh;
                int slot = meshRunIndex[(mesh.Index, mesh.Generation)];
                uint dst = writeCursor[slot]++;
                bool dissolving = inst.Dissolving;
                if (castKinds != null) castKinds[(int)dst] = ClassifyCaster(inst);
                instanceData[(int)dst] = new ModelRenderer.InstanceData
                {
                    Model = inst.World,
                    Tint = inst.Tint,
                    // During a dissolve the emissive channel carries the edge colour and Dissolve = (threshold, edge
                    // width) lights the gated ModelFrag term. A non-dissolving draw keeps the material emissive and a
                    // zero Dissolve, so it is byte-identical to the pre-dissolve packing (issue #253). SpecParams.z is
                    // left 0 for ApplyAlphaCutoffs to fill with the MASK cutoff, independent of dissolve.
                    Emissive = dissolving ? inst.DissolveEdge : inst.Material.Emissive,
                    SpecParams = new Vector4(inst.Material.Specular, inst.Material.Shininess, 0f, 0f),
                    Dissolve = dissolving ? new Vector2(inst.DissolveThreshold, inst.DissolveEdgeWidth) : Vector2.Zero,
                };
            }
        }

        // Fold each mesh's alpha-cutout threshold into its instances' SpecParams.z, reading the cutoff from the
        // loaded mesh slot (stale-handle runs resolve to 0 = no clip, matching the draw loop's stale skip). Thin
        // wrapper over the pure overload below so the cutoff lookup stays private to Scene3D. Reads _alphaCutoffLookup
        // (bound once at construction, issue #374) instead of building a new closure over _slots/_meshes on every
        // RenderInternal call: this runs once per rendered frame in a path documented allocation-free.
        void ApplyAlphaCutoffs(List<ModelRenderer.InstanceData> instanceData, List<MeshRun> runs)
            => ApplyAlphaCutoffs(instanceData, runs, _alphaCutoffLookup);

        // Internal (not private): lets KhaozEngine.Render.Tests (InternalsVisibleTo) call the exact per-frame
        // wrapper RenderInternal calls, without standing up a full render, to prove it allocates nothing (issue
        // #374). Needs a live Scene3D (GPU-backed), so the test is a GpuFact, but skips the render itself.
        internal void ApplyAlphaCutoffsForTest(List<ModelRenderer.InstanceData> instanceData, List<MeshRun> runs)
            => ApplyAlphaCutoffs(instanceData, runs);

        // The cutoff lookup body itself, bound once into _alphaCutoffLookup at construction rather than re-captured
        // per call.
        float AlphaCutoffFor(MeshHandle h) =>
            _slots.IsValid(h.Index, h.Generation) && _meshes[h.Index] is { } m ? m.AlphaCutoff : 0f;

        /// <summary>Write each run's mesh alpha-cutout threshold (<paramref name="cutoffFor"/>) into that run's
        /// contiguous slice of <paramref name="instanceData"/> at <c>SpecParams.z</c>, so the model fragment
        /// discards texels whose baseColor alpha is below it (MASK foliage renders as its silhouette). A run whose
        /// cutoff is 0 (OPAQUE, the default) is left untouched, so the instance data is byte-identical to the
        /// pre-cutout path. Pure over its inputs (no GPU), so the packing is headless-testable with a fake lookup.</summary>
        internal static void ApplyAlphaCutoffs(List<ModelRenderer.InstanceData> instanceData, List<MeshRun> runs,
            Func<MeshHandle, float> cutoffFor)
        {
            foreach (MeshRun run in runs)
            {
                float cutoff = cutoffFor(run.Mesh);
                if (cutoff <= 0f) continue;
                for (uint s = 0; s < run.Count; s++)
                {
                    int i = (int)(run.Start + s);
                    ModelRenderer.InstanceData d = instanceData[i];
                    d.SpecParams.Z = cutoff;
                    instanceData[i] = d;
                }
            }
        }
    }
}
