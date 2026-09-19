using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The one scene <see cref="PointShadowGpuTests"/> and <see cref="PointShadowFilterGpuTests"/> render every case
/// through, held for the whole class as a class fixture (the <c>OceanFocusScene</c> rule). A flat floor, a couple
/// of box casters and a camera looking almost straight down, so a world point on the floor maps to a picture pixel
/// through <see cref="IsoCamera3D.WorldToScreen"/> and every probe is named in world units.
/// <para>
/// xUnit builds ONE fixture instance PER TEST CLASS, so the two classes hold a scene each and a case that reframes
/// the camera (<see cref="FrameOn"/>) cannot reach the other class's captures.
/// </para>
/// </summary>
/// <remarks>
/// The device is built LAZILY on the first capture, so a plain <c>dotnet test</c> that skips every
/// <c>[GpuFact]</c> never asks for one. The globals are darkened to nothing so the picture is the point light's
/// own contribution: a frame lit by the key light would be equally bright shadowed and unshadowed, and every
/// relative assertion in the class would pass for no reason.
/// </remarks>
public sealed class PointShadowScene : IDisposable
{
    /// <summary>Capture size. Small on purpose: these run on lavapipe and WARP as well as Metal, and every claim
    /// is made about a handful of probes rather than about the picture.</summary>
    public const int Width = 192, Height = 192;

    /// <summary>The point light intensity the near cases use, chosen so the lit floor sits well clear of both
    /// black and saturation: a probe pinned at 255 could not show a shadow softening and one near 0 could not
    /// show acne.</summary>
    public const float Intensity = 1.6f;

    /// <summary>The intensity the off-camera case uses, which stands its light fourteen metres away.</summary>
    public const float FarIntensity = 20f;

    /// <summary>Where the off-camera pillar stands, well past the framed floor.</summary>
    public static Vector3 OffscreenPillarCentre => new(0f, 1.5f, 16f);

    GpuDeviceContext? _gpu;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    IGpuCommandList? _commands;
    Scene3D? _scene;
    MeshHandle _floor;
    MeshHandle _box;

    /// <summary>One captured frame: its pixels, the shadow diagnostics it published, and how many point lights it
    /// reported as carrying a map. Probes are named in WORLD units and resolved through the camera.</summary>
    public sealed class Shot(byte[] rgba, ShadowPassDiagnostics diagnostics, int shadowedLights, IIsoCamera3D camera)
    {
        public byte[] Rgba { get; } = rgba;
        public ShadowPassDiagnostics Diagnostics { get; } = diagnostics;
        public int ShadowedLights { get; } = shadowedLights;

        /// <summary>The red channel averaged over a 3 by 3 patch at <paramref name="world"/>, which is the
        /// brightness every assertion in the class is made in.</summary>
        public int Red(Vector3 world) => Channel(world, 0);

        /// <summary>The blue channel at <paramref name="world"/>, which the slot-order case reads separately from
        /// the red one.</summary>
        public int Blue(Vector3 world) => Channel(world, 2);

        int Channel(Vector3 world, int offset)
        {
            camera.WorldToScreen(world, Width, Height, out Vector2 px);
            int cx = Math.Clamp((int)px.X, 1, Width - 2);
            int cy = Math.Clamp((int)px.Y, 1, Height - 2);
            int sum = 0;
            for (int y = cy - 1; y <= cy + 1; y++)
                for (int x = cx - 1; x <= cx + 1; x++)
                    sum += Rgba[(y * Width + x) * 4 + offset];
            return sum / 9;
        }
    }

    /// <summary>What a case hands its frame: the queue calls it needs, plus the settings object it may tune. A
    /// thin façade over <see cref="Scene3D"/> so a case reads as a scene description rather than as matrix
    /// arithmetic.</summary>
    public sealed class Frame(Scene3D scene, MeshHandle floor, MeshHandle box)
    {
        /// <summary>This capture's point-shadow settings, reset to the shipped defaults before every capture, so
        /// a case that tunes one knob cannot reach the next case.</summary>
        public PointShadowSettings Settings => scene.Post.Quality.Shadows.PointShadows;

        /// <summary>The flat receiving floor, forty metres square at y = 0.</summary>
        public void DrawFloor() => scene.Draw(floor, Matrix4x4.Identity, new Color(1f, 1f, 1f, 1f));

        /// <summary>The wall: x in [2, 2.4], y in [0, 3], z in [-3, 3] plus <paramref name="zOffset"/>.</summary>
        public void DrawWall(float zOffset = 0f) => Box(0.4f, 3f, 6f, new Vector3(2.2f, 1.5f, zOffset));

        /// <summary>The same wall split in two, leaving z in [-0.5, 0.5] open.</summary>
        public void DrawSplitWall()
        {
            Box(0.4f, 3f, 2.5f, new Vector3(2.2f, 1.5f, 1.75f));
            Box(0.4f, 3f, 2.5f, new Vector3(2.2f, 1.5f, -1.75f));
        }

        /// <summary>A narrow pillar standing well outside the framed floor, between the far light and the middle
        /// of the picture.</summary>
        public void DrawOffscreenPillar() => Box(0.6f, 3f, 0.4f, OffscreenPillarCentre);

        /// <summary>The lamp's own closed body around the flame: a 20 cm cube CENTRED on the light, so the
        /// nearest surface in every direction is the fixture itself, 10 cm away at the faces and 17.3 cm at the
        /// corners. Every placed lamp model is this shape.</summary>
        public void DrawLampFixture(Vector3 centre) => Box(0.2f, 0.2f, 0.2f, centre);

        /// <summary>
        /// The same lamp with its BRACKET, as one mesh: a 20 cm column running from 60 cm below the flame to 10 cm
        /// above it, so the flame is closed in on every side and the piece as a whole reaches well past any
        /// sensible clearance.
        /// <para>
        /// That second half is why the cases use this rather than the bare cube. A bare cube lies entirely inside
        /// a 25 cm clearance, so the instance is dropped by the CPU sphere test and the fragment's own discard is
        /// never asked anything. Real fixtures hang off brackets and stand on posts, and the geometry that goes
        /// dark is only the part near the flame.
        /// </para>
        /// </summary>
        public void DrawLampOnItsBracket(Vector3 flame) =>
            Box(0.2f, 1.2f, 0.2f, flame - new Vector3(0f, 0.5f, 0f));

        /// <summary>Queue a point light with a shadow request.</summary>
        public void AddLight(Vector3 at, Color colour, float radius, float intensity, LightShadow shadow)
            => scene.AddLight(at, colour, radius, intensity, shadow);

        /// <summary>Queue a point light through the four-argument overload, which is the unshadowed path.</summary>
        public void AddLight(Vector3 at, Color colour, float radius, float intensity)
            => scene.AddLight(at, colour, radius, intensity);

        /// <summary>One box caster, <paramref name="sx"/> by <paramref name="sy"/> by <paramref name="sz"/> metres
        /// at <paramref name="centre"/>, white unless <paramref name="colour"/> says otherwise. Public so a
        /// sibling class can assemble a fixture of its own out of the same shared scene, and so a case that needs
        /// to tell one surface from another in the picture can give it its own albedo.</summary>
        public void Box(float sx, float sy, float sz, Vector3 centre, Color? colour = null) =>
            scene.Draw(box, Matrix4x4.CreateScale(sx, sy, sz) * Matrix4x4.CreateTranslation(centre),
                colour ?? new Color(1f, 1f, 1f, 1f));
    }

    /// <summary>Render ONE frame of <paramref name="describe"/> and read it back, under
    /// <paramref name="budget"/> (the shipped defaults when null).</summary>
    public Shot One(Action<Frame> describe, PointShadowSettings? budget = null)
    {
        ArgumentNullException.ThrowIfNull(describe);
        return Many(1, (f, _) => describe(f), budget)[0];
    }

    /// <summary>What the point-shadow atlas is live at, for the cases that change the budget.</summary>
    public PointShadowResolution Resolved
    {
        get
        {
            Device();
            return _scene!.ResolvedPointShadows;
        }
    }

    /// <summary>Where the camera stands, so a case that depends on which of two lights the budget ranks first can
    /// say so as a precondition rather than assume it.</summary>
    public Vector3 Eye
    {
        get
        {
            Device();
            return _scene!.Camera.Eye;
        }
    }

    /// <summary>Whether an atlas is allocated at all, which is what the released case asserts.</summary>
    public bool HasAtlas
    {
        get
        {
            Device();
            return _scene!.PointShadowTexture is not null;
        }
    }

    /// <summary>
    /// Render <paramref name="frames"/> consecutive frames of the same scene through ONE <see cref="Scene3D"/>
    /// and read every one of them back, which is how the cache cases watch a row go from rebuilt to reused.
    /// <paramref name="describe"/> receives the frame index.
    /// <para>
    /// EVERY CAPTURE STARTS COLD AND BURNS TWO FRAMES GETTING THERE, because allocating, reshaping and releasing
    /// the atlas is frame-BOUNDARY work. The first throwaway frame turns point shadows off, which gives the atlas
    /// back, so the scene the whole class shares cannot hand one case the layout and the cached rows another case
    /// left behind. The second is the WARM-UP: it asks for <paramref name="budget"/> with no atlas standing, so
    /// it renders unshadowed and the boundary after it brings the atlas up. What the cases then see is frame 1 of
    /// a fresh atlas, every time and in any order, which is what makes a rebuild counter mean something.
    /// </para>
    /// </summary>
    public IReadOnlyList<Shot> Many(int frames, Action<Frame, int> describe, PointShadowSettings? budget = null)
    {
        ArgumentNullException.ThrowIfNull(describe);
        IGpuDevice gd = Device();
        Scene3D scene = _scene!;
        var frame = new Frame(scene, _floor, _box);

        scene.RequestPointShadowSettings(new PointShadowSettings { Enabled = false });
        RenderFrame();                                                  // gives the atlas back
        scene.RequestPointShadowSettings(budget ?? new PointShadowSettings());
        Capture(0);                                                     // the warm-up, which allocates it again

        var shots = new List<Shot>(frames);
        for (int i = 0; i < frames; i++) shots.Add(Capture(i));
        return shots;

        Shot Capture(int index)
        {
            scene.Begin();
            describe(frame, index);
            scene.PrepareFrame();
            using (GpuRecording.Open(gd, _commands!, "PointShadowScene.Capture"))
                scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
            gd.Submit(_commands!);
            gd.WaitForIdle();
            return new Shot(GpuReadback.ToRgba(gd, _target!, Width, Height),
                scene.LastShadowPassDiagnostics, scene.PointShadowedLights, scene.Camera);
        }

        // An empty frame, which is all it takes to reach a boundary. Nothing is drawn and nothing is read back:
        // its whole job is to let the release land.
        void RenderFrame()
        {
            scene.Begin();
            scene.PrepareFrame();
            using (GpuRecording.Open(gd, _commands!, "PointShadowScene.Release"))
                scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
            gd.Submit(_commands!);
            gd.WaitForIdle();
        }
    }

    /// <summary>Where a world point lands in the capture, so a case can walk its probes ONE TEXEL at a time and
    /// read <see cref="Shot.Rgba"/> itself. <see cref="Shot.Red"/> averages a 3 by 3 patch, and that patch is a
    /// blur: an edge-width measurement must not add a blur to the thing it is measuring.</summary>
    public Vector2 Pixel(Vector3 world)
    {
        Device();
        _scene!.Camera.WorldToScreen(world, Width, Height, out Vector2 px);
        return px;
    }

    /// <summary>Frame a SMALL region, for a case that measures a shadow EDGE rather than comparing two probes.
    /// The standing framing puts about forty-six metres across 192 pixels, where a penumbra a few centimetres
    /// wide is a fraction of one texel and nothing can be counted. Holds for every later capture on this
    /// fixture instance, which is this class's own.</summary>
    public void FrameOn(Vector3 centre, float halfExtent)
    {
        Device();
        _scene!.Camera.Frame(centre, new Vector3(halfExtent * 2f, 0.2f, halfExtent * 2f), margin: 1f);
    }

    /// <summary>Whether a world point projects inside the picture, which is how the off-camera case states that
    /// its caster really is off camera rather than merely far away.</summary>
    public bool OnScreen(Vector3 world)
    {
        Device();
        _scene!.Camera.WorldToScreen(world, Width, Height, out Vector2 px);
        return px.X >= 0f && px.X < Width && px.Y >= 0f && px.Y < Height;
    }

    IGpuDevice Device()
    {
        if (_gpu is not null) return _gpu.GpuDevice;

        _gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice gd = _gpu.GpuDevice;
        IGpuResourceFactory factory = gd.Factory;
        _target = factory.CreateTexture(GpuTextureDescription.Texture2D(
            Width, Height, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = factory.CreateFramebuffer(null, _target);
        _commands = factory.CreateCommandList();
        _scene = new Scene3D(gd, _framebuffer.Outputs, null);
        _floor = _scene.LoadMesh(MeshPrimitives.Plane(40f, 40f));
        _box = _scene.LoadMesh(MeshPrimitives.Box(1f));
        Setup(_scene);
        return gd;
    }

    /// <summary>The framing and the lighting environment, applied once. Almost straight down (the camera is an
    /// orthographic iso one, and at this elevation a floor point's world x and z map to picture x and y), and
    /// every global light term off, so the picture is the point lights and nothing else.</summary>
    static void Setup(Scene3D scene)
    {
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.TransparentBackground = false;
        scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
        scene.Post.LightColor = new Color(0f, 0f, 0f, 1f);
        scene.Post.FillLightColor = new Color(0f, 0f, 0f, 1f);
        scene.Post.AmbientColor = new Color(0f, 0f, 0f, 1f);
        scene.Post.CelBands = 0;                        // banded ndl would quantise every probe level
        // ACES filmic tone mapping is channel-COUPLED (it preserves chroma across the three), so dimming one
        // light's contribution moves the other two channels with it. The slot-order case reads red and blue
        // separately, so the operator has to be off for those two numbers to mean one light each.
        scene.Post.Hdr.Enabled = false;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;   // the key light is not what this class measures
        scene.EffectTimeSeconds = 0f;
        scene.Camera.AspectRatio = (float)Width / Height;
        scene.Camera.Azimuth = 0f;
        scene.Camera.Elevation = 1.35f;                 // about 77 degrees: nearly plan view, minimal occlusion
        scene.Camera.Frame(Vector3.Zero, new Vector3(22f, 2f, 22f), margin: 1f);
    }

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
