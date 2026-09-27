using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The front orthographic stage most acceptance scenes use: the camera looks down minus Z at the XY plane, one
    /// display pixel is <see cref="PixelWorld"/> world units, and a wall stands at Z minus 2, either flat grey, striped
    /// or textured. Every setup loads the same meshes in the same order, so <see cref="Box"/> and <see cref="Panel"/>
    /// hold the same handles in every scene the stage sets up, a reference render between two frames included.
    /// Nothing in these scenes reads the clock or a random source, so a frame depends only on its frame number.
    /// </summary>
    internal sealed class FrontStage
    {
        /// <summary>Display pixels one texel of the textured wall spans, so every three by three neighbourhood holds
        /// its variation. A texel is magnified at a 1:1 internal size, in every supersampled reference and under the
        /// Quality and Balanced presets, one internal pixel under Performance, and minified under UltraPerformance,
        /// where the mip chain and the mip bias decide what it shows.</summary>
        public const float TexelPixels = 2f;

        /// <summary>The textured wall's texture is this many texels a side, repeating.</summary>
        public const int TextureSize = 64;

        const uint TextureSeed = 0x9E3779B9u;

        public readonly int W, H;
        public readonly float OrthoSize;
        public MeshHandle Box;
        public MeshHandle Panel;

        public FrontStage(int w, int h, float orthoSize) { W = w; H = h; OrthoSize = orthoSize; }

        public float PixelWorld => OrthoSize / H;

        public void Setup(Scene3D s, AntiAliasing aa, TemporalUpscale preset = TemporalUpscale.Native,
            float sharpness = 0.25f)
        {
            s.Post.UseSmoothPreset();
            s.Post.RenderScale = RenderScale.MatchViewport;
            s.Post.Quality.AntiAliasing = aa;
            s.Post.Temporal.Upscale = preset;
            s.Post.Temporal.Sharpness = sharpness;
            s.Post.TransparentBackground = false;
            s.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
            s.Post.AmbientColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            s.Camera.Azimuth = 0f; s.Camera.Elevation = 0f; s.Camera.AspectRatio = (float)W / H;
            s.Camera.OrthoSize = OrthoSize; s.Camera.Target = Vector3.Zero; s.Camera.Zoom = 1f;
            Box = s.LoadMesh(MeshPrimitives.Box(1f));
            Panel = s.LoadMesh(PanelMesh(), s.LoadTexture(NoiseTexture(), TextureSize, TextureSize));
        }

        /// <summary>A camera matching the stage's at <paramref name="targetX"/>, for footprints computed without a
        /// scene.</summary>
        public IsoCamera3D Camera(float targetX = 0f) => new()
        {
            Azimuth = 0f, Elevation = 0f, AspectRatio = (float)W / H, OrthoSize = OrthoSize, Zoom = 1f,
            Target = new Vector3(targetX, 0f, 0f),
        };

        /// <summary>The flat grey wall. It follows the camera across, so a pan shows no wall edge.</summary>
        public void Wall(Scene3D s) => s.Draw(Box,
            Matrix4x4.CreateScale(OrthoSize * 6f, OrthoSize * 3f, 0.1f)
            * Matrix4x4.CreateTranslation(s.Camera.Target.X, 0f, -2f), new Color(0.45f, 0.5f, 0.55f, 1f));

        /// <summary>The wall with darker vertical stripes every 0.5 world units, so a pan is visible.</summary>
        public void StripedWall(Scene3D s)
        {
            Wall(s);
            for (int i = -40; i <= 40; i++)
                s.Draw(Box, Matrix4x4.CreateScale(0.12f, OrthoSize * 3f, 0.05f)
                    * Matrix4x4.CreateTranslation(i * 0.5f, 0f, -1.9f), new Color(0.25f, 0.28f, 0.32f, 1f));
        }

        /// <summary>
        /// A textured wall in place of the flat one: a world-fixed panel at the flat wall's front face, covering the
        /// flat wall's extent around the origin, whose texels are independent greys from 64 to 191 of 255, each
        /// <see cref="TexelPixels"/> display pixels across. It stays put under a pan. Its variation widens the
        /// resolve's neighbourhood clip, which a flat wall leaves narrow. Draw it instead of <see cref="Wall"/>.
        /// </summary>
        public void TexturedWall(Scene3D s) => s.Draw(Panel, Matrix4x4.Identity);

        public PixelRect Footprint(Scene3D s, Vector3 centre, Vector3 size) =>
            TemporalAcceptance.Footprint(s.Camera, centre - size * 0.5f, centre + size * 0.5f, W, H);

        // Both windings, so culling cannot hide the panel. Row 0 of the texture is at the top.
        GltfMesh PanelMesh()
        {
            float x = OrthoSize * 3f, y = OrthoSize * 1.5f, z = -1.95f;
            float period = TexelPixels * PixelWorld * TextureSize;
            var n = Vector3.UnitZ;
            var c = Vector4.One;
            ModelVertex V(float px, float py) =>
                new(new Vector3(px, py, z), n, c, new Vector2(px / period, -py / period));
            var v = new[] { V(-x, -y), V(x, -y), V(x, y), V(-x, y) };
            return new GltfMesh(v, new ushort[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 });
        }

        static byte[] NoiseTexture()
        {
            var rgba = new byte[TextureSize * TextureSize * 4];
            for (uint y = 0; y < TextureSize; y++)
                for (uint x = 0; x < TextureSize; x++)
                {
                    uint h = x * 0x8DA6B343u ^ y * 0xD8163841u ^ TextureSeed;
                    h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                    byte grey = (byte)(64 + h % 128);
                    int i = (int)(y * TextureSize + x) * 4;
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = grey;
                    rgba[i + 3] = 255;
                }
            return rgba;
        }
    }

    /// <summary>Thin geometry: 21 bars a third of a pixel wide at 30 degrees, the sub-pixel case that aliases. A pan
    /// moves the camera, never the bars. <see cref="Draw"/> puts them over the dark background, which reprojects by
    /// the camera's rotation, the worst case for the resolve's lock. <see cref="DrawOverWall"/> puts them over the flat
    /// wall, bars over a surface as on an isometric ground.</summary>
    internal sealed class FenceScene
    {
        public readonly FrontStage Stage;
        public FenceScene(int w, int h) => Stage = new FrontStage(w, h, 4.5f);

        /// <summary>The same bars and pan in front of the flat grey wall.</summary>
        public void DrawOverWall(Scene3D s, float panPixels)
        {
            Draw(s, panPixels);
            Stage.Wall(s);
        }

        public void Draw(Scene3D s, float panPixels)
        {
            float px = Stage.PixelWorld;
            s.Camera.Target = new Vector3(panPixels * px, 0f, 0f);
            for (int i = 0; i < 21; i++)
            {
                float x = -3.5f + 7f * i / 20f;
                s.Draw(Stage.Box, Matrix4x4.CreateScale(0.35f * px, 3.6f, 0.35f * px) * Matrix4x4.CreateRotationZ(0.52f)
                    * Matrix4x4.CreateTranslation(x, 0f, 0f), new Color(0.95f, 0.95f, 0.95f, 1f));
            }
        }

        public PixelRect Region => new(Stage.W / 8, Stage.H / 8, Stage.W * 7 / 8, Stage.H * 7 / 8);
    }

    /// <summary>The engine's default isometric camera over a ground grid of third-of-a-pixel lines and three
    /// boxes, zooming. Zoom is a projection change, which the motion target has to carry. The zoom holds at 1 for
    /// <see cref="HoldFrames"/> frames, then runs one sine cycle of amplitude 0.25 every <see cref="CycleFrames"/>
    /// frames.</summary>
    internal sealed class IsoYard
    {
        public const int HoldFrames = 16, CycleFrames = 48;
        public readonly int W, H;
        MeshHandle _box;

        public IsoYard(int w, int h) { W = w; H = h; }

        public float ZoomAt(int n) =>
            n < HoldFrames ? 1f : 1f + 0.25f * MathF.Sin(2f * MathF.PI * (n - HoldFrames) / CycleFrames);

        public void Setup(Scene3D s, AntiAliasing aa)
        {
            s.Post.UseSmoothPreset();
            s.Post.RenderScale = RenderScale.MatchViewport;
            s.Post.Quality.AntiAliasing = aa;
            s.Post.TransparentBackground = false;
            s.Post.AmbientColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            s.Camera.AspectRatio = (float)W / H;
            s.Camera.OrthoSize = 10f;
            s.Camera.Target = Vector3.Zero;
            _box = s.LoadMesh(MeshPrimitives.Box(1f));
        }

        public void DrawAtZoom(Scene3D s, float zoom)
        {
            s.Camera.Zoom = zoom;
            float line = 0.35f * 10f / H;
            var ground = new Color(0.55f, 0.6f, 0.5f, 1f);
            var ink = new Color(0.15f, 0.15f, 0.12f, 1f);
            s.Draw(_box, Matrix4x4.CreateScale(16f, 0.1f, 16f) * Matrix4x4.CreateTranslation(0f, -0.05f, 0f), ground);
            for (int i = -8; i <= 8; i++)
            {
                s.Draw(_box, Matrix4x4.CreateScale(16f, 0.02f, line) * Matrix4x4.CreateTranslation(0f, 0.01f, i), ink);
                s.Draw(_box, Matrix4x4.CreateScale(line, 0.02f, 16f) * Matrix4x4.CreateTranslation(i, 0.01f, 0f), ink);
            }
            s.Draw(_box, Matrix4x4.CreateTranslation(-3f, 0.5f, 2f), new Color(0.95f, 0.9f, 0.8f, 1f));
            s.Draw(_box, Matrix4x4.CreateScale(1f, 2f, 1f) * Matrix4x4.CreateTranslation(2f, 1f, -1f),
                new Color(0.9f, 0.4f, 0.3f, 1f));
            s.Draw(_box, Matrix4x4.CreateTranslation(4f, 0.5f, 3f), new Color(0.3f, 0.5f, 0.9f, 1f));
        }

        public void Draw(Scene3D s, int n) => DrawAtZoom(s, ZoomAt(n));
    }

    /// <summary>
    /// The ghosting case: a red box 30 display pixels square crossing the view left to right at
    /// <see cref="PixelsPerFrame"/> display pixels a frame, keyed or not, over the flat or the textured wall. Only the
    /// wall draws for the first <see cref="StillFrames"/> frames, so the history holds the bare wall when the box
    /// appears. Its shadow falls below the view. <see cref="Background"/> is the same path without the box, for the
    /// trail metric's background frame at the same frame index and jitter.
    /// </summary>
    internal sealed class CrossingScene
    {
        public const int StillFrames = 16;
        public const float PixelsPerFrame = 4f;
        public const ulong Key = 11;
        public static readonly Vector3 Size = new(0.75f, 0.75f, 0.5f);
        public static readonly Color Tint = new(1f, 0.25f, 0.2f, 1f);

        public readonly FrontStage Stage;
        public readonly bool Keyed, Textured;

        public CrossingScene(int w, int h, bool keyed, bool textured)
        {
            Stage = new FrontStage(w, h, 4.5f);
            Keyed = keyed;
            Textured = textured;
        }

        /// <summary>The box's centre on frame <paramref name="n"/>. It starts moving on the frame it appears.</summary>
        public Vector3 At(int n) =>
            new(-2.5f + Math.Max(0, n - StillFrames) * PixelsPerFrame * Stage.PixelWorld, 0f, 0f);

        public void Background(Scene3D s, int n)
        {
            if (Textured) Stage.TexturedWall(s);
            else Stage.Wall(s);
        }

        public void Draw(Scene3D s, int n)
        {
            Background(s, n);
            if (n < StillFrames) return;
            Matrix4x4 world = Matrix4x4.CreateScale(Size) * Matrix4x4.CreateTranslation(At(n));
            if (Keyed) s.Draw(new RigidInstanceDraw(Stage.Box, world) { Tint = Tint, Motion = MotionKey.From(Key) });
            else s.Draw(Stage.Box, world, Tint);
        }

        /// <summary>The box's rectangle on <paramref name="frame"/> and each of the <paramref name="count"/> minus 1
        /// frames before it, newest first, as <see cref="TemporalAcceptance.Trail"/> reads them. A frame before the box
        /// appeared has an empty rectangle.</summary>
        public IReadOnlyList<PixelRect> Footprints(int frame, int count = 9)
        {
            IsoCamera3D camera = Stage.Camera();
            var rects = new PixelRect[count];
            for (int k = 0; k < count; k++)
            {
                int n = frame - k;
                if (n < StillFrames) continue;
                Vector3 c = At(n);
                rects[k] = TemporalAcceptance.Footprint(camera, c - Size * 0.5f, c + Size * 0.5f, Stage.W, Stage.H);
            }
            return rects;
        }
    }

    /// <summary>
    /// The disocclusion case: a keyed red box 60 display pixels square covers <see cref="Covered"/> over the flat or
    /// the textured wall until <see cref="RevealFrame"/>, then jumps 3 m to the right to <see cref="Landed"/>, 60
    /// pixels clear of where it was, so the revealed wall has only the box in its history. Its shadow falls below the
    /// view. <see cref="Background"/> is the same path without the box.
    /// </summary>
    internal sealed class RevealScene
    {
        public const int RevealFrame = 32;
        public const ulong Key = 21;
        public static readonly Vector3 Size = new(1.5f, 1.5f, 0.5f);
        public static readonly Vector3 From = new(-1f, 0f, 0f), To = new(2f, 0f, 0f);
        public static readonly Color Tint = new(1f, 0.1f, 0.1f, 1f);

        public readonly FrontStage Stage;
        public readonly bool Textured;

        public RevealScene(int w, int h, bool textured)
        {
            Stage = new FrontStage(w, h, 4.5f);
            Textured = textured;
        }

        public void Background(Scene3D s, int n)
        {
            if (Textured) Stage.TexturedWall(s);
            else Stage.Wall(s);
        }

        public void Draw(Scene3D s, int n)
        {
            Background(s, n);
            Matrix4x4 world = Matrix4x4.CreateScale(Size) * Matrix4x4.CreateTranslation(n < RevealFrame ? From : To);
            s.Draw(new RigidInstanceDraw(Stage.Box, world) { Tint = Tint, Motion = MotionKey.From(Key) });
        }

        /// <summary>Every pixel the box touched before the reveal.</summary>
        public PixelRect Covered => Rect(From);

        /// <summary>Every pixel the box touches from the reveal on.</summary>
        public PixelRect Landed => Rect(To);

        /// <summary>The pixels the box covered before the reveal, less a one-pixel border, which show only the wall
        /// after it.</summary>
        public PixelRect Revealed => Covered.Inflate(-1);

        PixelRect Rect(Vector3 centre) =>
            TemporalAcceptance.Footprint(Stage.Camera(), centre - Size * 0.5f, centre + Size * 0.5f, Stage.W, Stage.H);
    }

    /// <summary>
    /// A prop mid-fade: a 6 m box on a 24 m tall view, 45 display pixels across at 320 by 180, so the dissolve's
    /// noise cells are near pixel size. It is drawn solid, dissolving, or as a LOD crossfade, where a second copy in
    /// another colour takes the exact complement of the dissolve's noise. The camera pans, the prop stays put.
    /// </summary>
    internal sealed class FadeProp
    {
        public const float PanPixelsPerFrame = 0.2f;
        public static readonly Vector3 Size = new(6f, 6f, 1f);
        public static readonly Color Near = new(0.95f, 0.85f, 0.6f, 1f), Far = new(0.2f, 0.35f, 0.8f, 1f);

        public readonly FrontStage Stage;
        public FadeProp(int w, int h) => Stage = new FrontStage(w, h, 24f);

        /// <summary>The flat wall alone, with the camera panned <paramref name="panPixels"/> display pixels.</summary>
        public void Background(Scene3D s, float panPixels)
        {
            s.Camera.Target = new Vector3(panPixels * Stage.PixelWorld, 0f, 0f);
            Stage.Wall(s);
        }

        /// <summary>The prop at <paramref name="dissolve"/>, 0 solid to 1 gone, over the wall, with the camera panned
        /// <paramref name="panPixels"/> display pixels. <paramref name="crossfade"/> adds the complementary
        /// copy.</summary>
        public void Draw(Scene3D s, float panPixels, float dissolve, bool crossfade)
        {
            Background(s, panPixels);
            Matrix4x4 world = Matrix4x4.CreateScale(Size);
            if (dissolve <= 0f) { s.Draw(Stage.Box, world, Near); return; }
            s.Draw(new RigidInstanceDraw(Stage.Box, world)
            {
                Tint = Near, Dissolve = dissolve, DissolveEdgeWidth = 0.001f, DissolveEdgeColor = Color.Black,
            });
            if (crossfade)
                s.Draw(new RigidInstanceDraw(Stage.Box, world)
                {
                    Tint = Far, Dissolve = dissolve, DissolveEdgeWidth = 0.001f, DissolveEdgeColor = Color.Black,
                    DissolveComplement = 1f,
                });
        }

        /// <summary>The pixels the prop fully covers at every pan from 0 to <paramref name="panPixels"/>, so a region
        /// measured through a pan holds prop and never wall.</summary>
        public PixelRect Region(float panPixels)
        {
            PixelRect a = Rect(0f), b = Rect(panPixels);
            return new PixelRect(Math.Max(a.X0, b.X0), Math.Max(a.Y0, b.Y0), Math.Min(a.X1, b.X1), Math.Min(a.Y1, b.Y1))
                .Inflate(-1);
        }

        PixelRect Rect(float panPixels) => TemporalAcceptance.Footprint(Stage.Camera(panPixels * Stage.PixelWorld),
            -Size * 0.5f, Size * 0.5f, Stage.W, Stage.H);
    }
}
