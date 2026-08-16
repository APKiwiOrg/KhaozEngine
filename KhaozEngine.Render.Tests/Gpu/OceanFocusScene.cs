using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The one open-water scene <see cref="OceanFocusGpuTests"/> renders all of its configurations through, held
    /// alive for the whole class as an xUnit <c>IClassFixture</c>.
    /// <para>
    /// <b>Why it exists (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/332">#332</see>).</b> That
    /// class captured 23 pictures, and every capture stood up its own <see cref="Scene3D"/>. Measured on Metal,
    /// a capture costs 2571 ms of which 2570 ms is the <see cref="Scene3D"/> constructor building the frame's
    /// pipelines: the device itself is under a millisecond, the two frames and the readback are 99 ms, and the
    /// FFT ocean's compute dispatches are 93 ms of that 99. A capture through an ALREADY-BUILT scene costs 3 ms.
    /// So the cost the issue attributed to re-running the compute dispatches is really pipeline creation, and it
    /// is the software legs' shader compilers that make it hurt there rather than anything about the ocean.
    /// Building the scene once and re-rendering it is therefore worth roughly 850x per capture, and reducing the
    /// ocean's resolution or cascade count would have bought back 4 per cent of the wrong number.
    /// </para>
    /// <para>
    /// <b>What re-rendering one scene costs in rigour, and what pays it back.</b> A reused scene carries state a
    /// fresh one does not: the FFT foam accumulator, the ping-ponged row intermediates and the frame clock all
    /// survive a frame by design (see <c>OceanFftProducer</c>), so "capture A then capture B" is not obviously
    /// the same picture as "capture B on its own". Nothing here assumes it is.
    /// <see cref="CaptureOnItsOwnDevice"/> renders the identical scene through the untouched public
    /// <see cref="Render3DSnapshot.Capture(int,int,Action{Scene3D},Action{Scene3D},int,ShadowSettings?)"/> path -
    /// its own device, its own scene, no history at all - and
    /// <see cref="OceanFocusGpuTests.TheSameSamplingFrameRendersTheSamePictureTwice"/> deliberately ages the
    /// shared scene through several other configurations first and then pins the two byte-for-byte. That test is
    /// the licence for every other capture in the class, which is why it is the one test that still pays for a
    /// second scene.
    /// </para>
    /// <para>
    /// <b>This is the assembly's first class fixture</b>, so it is kept deliberately small: it owns a device, a
    /// render target, a command list and one scene, and it hands out pictures. It holds no process-global state
    /// and swaps no ambient static, so it needs no <c>DisableParallelization</c> collection (the rule in
    /// <c>AGENTS.md</c> is about statics other classes can read). xUnit runs the methods of one test class
    /// sequentially, so the single scene is only ever driven by one test at a time, and the fixture's lifetime is
    /// exactly that class's.
    /// </para>
    /// <para>
    /// <b>The device is created lazily, on the first capture.</b> A fixture constructor that built a device would
    /// run on a machine with no GPU at all and turn a plain <c>dotnet test</c> - where every <c>[GpuFact]</c> in
    /// the class is skipped and no capture is ever asked for - into a class-wide construction error.
    /// </para>
    /// </summary>
    public sealed class OceanFocusScene : IDisposable
    {
        /// <summary>Capture width. Small on purpose: these run on lavapipe and WARP as well as Metal, and none of
        /// the claims need a big picture.</summary>
        public const int Width = 320;

        /// <summary>Capture height.</summary>
        public const int Height = 240;

        /// <summary>Frames rendered per capture. Two, because the ocean's row pass is dispatched a frame ahead of
        /// the column pass that consumes it, so the first frame primes and the second is the picture.</summary>
        public const int Frames = 2;

        GpuDeviceContext? _gpu;
        IGpuTexture? _target;
        IGpuFramebuffer? _framebuffer;
        IGpuCommandList? _commands;
        Scene3D? _scene;
        MeshHandle _seabed;

        /// <summary>Whether the device this class renders on can run compute at all. False means the scene would
        /// silently fall back to the procedural surface, so every assertion in the class would stop being about
        /// the FFT sampling frame. Reads the SHARED device, so asking costs nothing.</summary>
        public bool SupportsCompute => Device().Capabilities.SupportsCompute;

        /// <summary>The backend under test, for the message a failed compute check prints.</summary>
        public GpuBackendKind Backend => Device().Backend;

        /// <summary>
        /// Render one sea-state configuration through the SHARED scene and read the picture back. The sea state is
        /// replaced with a fresh <see cref="WaterSeaState"/> first, so a knob one test wrote cannot reach the
        /// next: what the caller gets is the shipped defaults, then <see cref="BaseSea"/>, then its own overrides,
        /// which is exactly the order a fresh scene applies them in.
        /// </summary>
        public byte[] Capture(Action<WaterSeaState> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            IGpuDevice gd = Device();
            Scene3D scene = _scene!;

            scene.Post.Water.SeaState = new WaterSeaState();
            BaseSea(scene.Post.Water.SeaState);
            configure(scene.Post.Water.SeaState);

            for (int i = 0; i < Frames; i++)
            {
                scene.Begin();
                DrawFrame(scene, _seabed);
                // Every producer with GPU work of its own runs here, between the queue being filled and the
                // frame's list being opened - the same ordering Render3DSnapshot.Capture uses, and the one
                // Direct3D 11's immediate-context mode requires (#423).
                scene.PrepareFrame();
                using (GpuRecording.Open(gd, _commands!, "OceanFocusScene.Capture"))
                    scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
                gd.Submit(_commands!);
            }
            gd.WaitForIdle();
            return GpuReadback.ToRgba(gd, _target!, Width, Height);
        }

        /// <summary>
        /// Render the SAME scene the way the class used to render every one of them: through the public
        /// <see cref="Render3DSnapshot"/> helper, which builds its own device and its own <see cref="Scene3D"/>
        /// and disposes both. The control against which the shared scene's output is pinned, and the reason
        /// nothing in this class rests on an unstated claim about reuse.
        /// </summary>
        public static byte[] CaptureOnItsOwnDevice(Action<WaterSeaState> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            MeshHandle seabed = default;
            return Render3DSnapshot.Capture(Width, Height,
                setup: scene =>
                {
                    seabed = Setup(scene);
                    configure(scene.Post.Water.SeaState);
                },
                drawFrame: scene => DrawFrame(scene, seabed),
                frames: Frames);
        }

        /// <summary>How the sea state is configured before a test's own overrides. Deliberately the shipped
        /// defaults except for the two knobs that make this affordable on a software rasterizer.</summary>
        public static void BaseSea(WaterSeaState sea)
        {
            ArgumentNullException.ThrowIfNull(sea);
            sea.Seed = 20260726;
            sea.CascadeCount = 2;
            sea.CascadeResolution = 64;
        }

        /// <summary>The scene: open water from an ELEVATED vantage, which is where the tiling this class is about
        /// actually reads, framed so the surface fills most of the picture. Returns the seabed mesh the frame
        /// draws under it.</summary>
        static MeshHandle Setup(Scene3D scene)
        {
            MeshHandle seabed = scene.LoadMesh(MeshPrimitives.Tile(400f, 1f));
            scene.Post.Starfield = false;
            scene.Post.Outline = true;
            scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            scene.Post.Sky.Enabled = true;
            scene.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
            scene.Post.Sky.HorizonColor = new Color(0.66f, 0.72f, 0.80f, 1f);
            scene.Post.Sky.ZenithColor = new Color(0.20f, 0.40f, 0.72f, 1f);
            scene.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);

            scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
            BaseSea(scene.Post.Water.SeaState);

            scene.Camera.Frame(new Vector3(0f, -10f, 0f), new Vector3(0f, 35f, 150f));
            scene.EffectTimeSeconds = 0f;
            return seabed;
        }

        static void DrawFrame(Scene3D scene, MeshHandle seabed)
        {
            scene.Draw(seabed, Matrix4x4.CreateTranslation(0f, -14f, 0f), new Color(0.18f, 0.20f, 0.18f, 1f));
            scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 300f));
        }

        IGpuDevice Device()
        {
            if (_gpu is not null) return _gpu.GpuDevice;

            _gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = _gpu.GpuDevice;
            IGpuResourceFactory factory = gd.Factory;
            _target = factory.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)Width, (uint)Height, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _framebuffer = factory.CreateFramebuffer(null, _target);
            _commands = factory.CreateCommandList();
            _scene = new Scene3D(gd, _framebuffer.Outputs, null);
            _seabed = Setup(_scene);
            return gd;
        }

        /// <summary>Torn down in the reverse of the order <see cref="Render3DSnapshot"/> tears its own down, and
        /// only after the last capture's <c>WaitForIdle</c>, so nothing is destroyed against queued work.</summary>
        public void Dispose()
        {
            _commands?.Dispose();
            _scene?.Dispose();
            _framebuffer?.Dispose();
            _target?.Dispose();
            _gpu?.Dispose();
            _commands = null;
            _scene = null;
            _framebuffer = null;
            _target = null;
            _gpu = null;
        }
    }
}
