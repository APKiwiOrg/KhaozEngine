using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A live, reusable offscreen 3D render that produces a sampleable <see cref="Texture2D"/>, so a 2D pass
    /// (a <see cref="SpriteBatch"/> / Gui panel) can draw a rotating 3D model preview - unit inspectors, shop /
    /// character-select previews, item icons.
    /// </summary>
    /// <remarks>
    /// Built once from the live <see cref="AppWindow"/> device (the same device the on-screen
    /// <see cref="Render3DSurface"/> and <see cref="Render2D.Render2DSurface"/> use), so the texture it returns is
    /// directly sampleable by the 2D batch - no separate device, no CPU readback (unlike
    /// <see cref="Render3DSnapshot"/>, which is for goldens/tooling).
    ///
    /// It owns a dedicated <see cref="Scene3D"/> isolated from the game's board scene, plus a single offscreen
    /// render target reused every frame (so a spinning preview allocates no GPU texture per frame). Load preview
    /// meshes and configure the camera/post ONCE via <see cref="Scene"/>, then each frame call
    /// <see cref="Capture"/> with the per-frame world transform(s); the same <see cref="Texture"/> is re-rendered
    /// in place and returned.
    ///
    /// Defaults to a transparent background (<see cref="PixelPostProcessSettings.TransparentBackground"/>) with the
    /// starfield off, so the model composites cleanly into a panel. The full stylized post chain still runs;
    /// frame the camera to the mesh bounds with <see cref="IsoCamera3D.Frame"/> and drive rotation by passing a
    /// world matrix per frame. Caller owns the instance and must <see cref="Dispose"/> it.
    /// </remarks>
    public sealed class Render3DPreview : IDisposable
    {
        /// <summary>Upper bound (per axis) on the offscreen target size, so a bogus panel size can't allocate an
        /// unbounded texture. Sizes are clamped into <c>[1, MaxDimension]</c> by <see cref="ClampSize"/>.</summary>
        public const int MaxDimension = 4096;

        readonly IGpuDevice _gd;
        readonly IGpuCommandList _cl;
        IGpuTexture _target = null!;
        IGpuFramebuffer _fb = null!;
        Texture2D _texture = null!;

        /// <summary>The dedicated scene rendered into the preview target. Load meshes/textures and configure the
        /// <see cref="Scene3D.Camera"/> / <see cref="Scene3D.Post"/> on this once; it is isolated from any board
        /// scene. Defaults: <see cref="PixelPostProcessSettings.TransparentBackground"/> on, starfield off.</summary>
        public Scene3D Scene { get; }

        /// <summary>Current target width (physical pixels), after <see cref="ClampSize"/>.</summary>
        public int Width { get; private set; }
        /// <summary>Current target height (physical pixels), after <see cref="ClampSize"/>.</summary>
        public int Height { get; private set; }

        /// <summary>The sampleable preview texture. A stable, non-owning wrapper over the reused offscreen target
        /// (the preview owns and disposes the underlying GPU texture); the same instance is returned by every
        /// <see cref="Capture"/> until <see cref="Resize"/> or <see cref="Dispose"/>.</summary>
        public Texture2D Texture => _texture;

        /// <summary>Create a preview on the window's live GPU device at <paramref name="width"/> x
        /// <paramref name="height"/> (clamped via <see cref="ClampSize"/>).</summary>
        public Render3DPreview(AppWindow window, int width, int height)
            : this(window.GpuDevice, width, height) { }

        // Device ctor: lets headless tests build a preview without a window (the public surface goes through
        // AppWindow, mirroring Render3DSurface).
        internal Render3DPreview(IGpuDevice gd, int width, int height)
        {
            _gd = gd;
            (Width, Height) = ClampSize(width, height);
            _cl = gd.Factory.CreateCommandList();
            AllocTarget(Width, Height);
            Scene = new Scene3D(gd, _fb.Outputs);
            // Sensible preview defaults: composite transparently into a panel; the starfield would fill the
            // background opaquely, so leave it off (callers can re-enable any post setting on Scene.Post).
            Scene.Post.TransparentBackground = true;
            Scene.Post.Starfield = false;
        }

        /// <summary>Clamp a requested target size into <c>[1, <see cref="MaxDimension"/>]</c> per axis. Pure +
        /// headless-testable (no GPU); mirrors the spirit of <see cref="Scene3D.ComputeTargetSize"/>.</summary>
        public static (int W, int H) ClampSize(int width, int height) =>
            (Math.Clamp(width, 1, MaxDimension), Math.Clamp(height, 1, MaxDimension));

        void AllocTarget(int w, int h)
        {
            var f = _gd.Factory;
            _target = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)w, (uint)h, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _fb = f.CreateFramebuffer(null, _target);   // colour-only (depth lives in Scene3D's internal MRT)
            _texture = Texture2D.Wrap(_target, w, h, ownsHandle: false);
        }

        /// <summary>Resize the offscreen target (e.g. the panel changed size). A no-op when the clamped size is
        /// unchanged. Invalidates the previous <see cref="Texture"/> instance.</summary>
        public void Resize(int width, int height)
        {
            var (w, h) = ClampSize(width, height);
            if (w == Width && h == Height) return;
            _texture.Dispose();                 // non-owning wrapper: frees nothing, just drops the stale wrapper
            _fb.Dispose();
            _target.Dispose();
            Width = w; Height = h;
            AllocTarget(w, h);
        }

        /// <summary>
        /// Render one preview frame into the (reused) offscreen target and return the sampleable
        /// <see cref="Texture"/>. Clears the scene's instance queue, invokes <paramref name="drawFrame"/> to queue
        /// the preview instance(s) (typically one <see cref="Scene3D.Draw(MeshHandle,System.Numerics.Matrix4x4)"/>
        /// with the current rotation), records the scene on its own command list, and submits.
        /// </summary>
        /// <remarks>
        /// Does NOT block the CPU. The preview runs on the same device/queue as the on-screen passes, so a later
        /// same-frame 2D pass that samples <see cref="Texture"/> sees the finished render by submission ordering
        /// (the preview submit precedes the 2D submit; the GPU serializes them). A CPU read of the result instead
        /// goes through <see cref="GpuReadback"/>, which fences itself. Avoiding a per-frame <c>WaitForIdle</c> here
        /// means N live previews no longer cost N full pipeline stalls per frame.
        /// </remarks>
        public Texture2D Capture(Action<Scene3D> drawFrame)
        {
            Scene.Begin();
            drawFrame?.Invoke(Scene);
            _cl.Begin();
            Scene.RenderInternal(_cl, Width, Height, _fb);
            _cl.End();
            _gd.Submit(_cl);
            return _texture;
        }

        public void Dispose()
        {
            Scene.Dispose();
            _cl.Dispose();
            _fb.Dispose();
            _target.Dispose();
        }
    }
}
