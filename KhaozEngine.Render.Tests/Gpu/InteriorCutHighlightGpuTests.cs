using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class InteriorCutHighlightGpuTests : IClassFixture<InteriorCutHighlightGpuTests.Stage>
{
    readonly Stage _stage;
    public InteriorCutHighlightGpuTests(Stage stage) => _stage = stage;

    [GpuFact]
    public void BothOutlineCoverageTargetsCut()
    {
        foreach (MeshOutlineOcclusion occlusion in new[] { MeshOutlineOcclusion.SceneDepth, MeshOutlineOcclusion.None })
        {
            byte[] pixels = _stage.Frame(Stage.Field, true, occlusion: occlusion);
            float[] full = _stage.Coverage("_fullCoverage");
            float[]? visible = occlusion == MeshOutlineOcclusion.SceneDepth ? _stage.Coverage("_visibleCoverage") : null;
            int inside = 0, outside = 0, boundaryRim = 0;
            for (int y = 0; y < Stage.H; y++)
                for (int x = 0; x < Stage.W; x++)
                {
                    Vector2 p = Stage.World(x, y);
                    int at = y * Stage.W + x;
                    if (p.X > .1f && p.X < .8f && MathF.Abs(p.Y) < .4f)
                    {
                        Assert.Equal(0f, full[at]);
                        if (visible is not null) Assert.Equal(0f, visible[at]);
                        Assert.False(IsRim(pixels, at));
                        inside++;
                    }
                    if (p.X < -.1f && p.X > -.8f && MathF.Abs(p.Y) < .4f)
                    {
                        Assert.Equal(1f, full[at]);
                        if (visible is not null) Assert.Equal(1f, visible[at]);
                        outside++;
                    }
                    if (p.X >= 0 && p.X < .06f && MathF.Abs(p.Y) < .35f && IsRim(pixels, at)) boundaryRim++;
                }
            Assert.True(inside > 500 && outside > 500);
            Assert.True(boundaryRim > 20, $"cut boundary rim pixels {boundaryRim}, mode {occlusion}");
        }
    }

    [GpuFact]
    public void SilhouetteCutsTheExtrudedHull()
    {
        foreach (bool temporal in new[] { false, true })
        {
            byte[] solid = _stage.Frame(null, true, silhouette: true, temporal: temporal, model: false);
            byte[] cut = _stage.Frame(Stage.HullField, true, silhouette: true, temporal: temporal, model: false);
            Vector2 jitter = _stage.Scene.CurrentFrameView.JitterPixels;
            int hullProbes = 0, cutProbes = 0, keptProbes = 0;
            for (int y = 0; y < Stage.H; y++)
                for (int x = 0; x < Stage.W; x++)
                {
                    Vector2 p = Stage.World(x, y, temporal ? jitter : Vector2.Zero);
                    int at = y * Stage.W + x;
                    if (MathF.Abs(p.Y) > .6f || !IsRim(solid, at)) continue;
                    if (p.X > 1.015625f)
                    {
                        Assert.False(IsRim(cut, at));
                        hullProbes++;
                    }
                    if (p.X > .1f && p.X < .7f)
                    {
                        Assert.False(IsRim(cut, at));
                        cutProbes++;
                    }
                    if (p.X < -.1f)
                    {
                        Assert.True(IsRim(cut, at));
                        keptProbes++;
                    }
                }
            Assert.True(hullProbes > 10, $"extruded-only probes {hullProbes}");
            Assert.True(cutProbes > 500 && keptProbes > 500);
        }
    }

    [GpuFact]
    public void HighlightOptOutIsByteIdentical()
    {
        foreach (bool temporal in new[] { false, true })
            foreach (bool silhouette in new[] { false, true })
            {
                byte[] noFieldPixels = _stage.Frame(null, false, silhouette, temporal);
                byte[] flagOffPixels = _stage.Frame(Stage.Field, false, silhouette, temporal);
                Assert.Equal(noFieldPixels, flagOffPixels);
            }
    }

    [GpuFact]
    public void ReusedSceneMatchesFreshAfterHighlightAndFieldChanges()
    {
        _stage.BoundaryFrame(null, new Vector3(4096f, .4f, 4096f), new Vector3(4096f, 128f, 4096f),
            false, BoundaryMode.SilhouetteTemporal);
        _stage.Frame(Stage.Field, true);
        _stage.Frame(Stage.HullField, true, silhouette: true, temporal: true);
        _stage.Frame(null, true);
        byte[] reused = _stage.Frame(Stage.Field, false, silhouette: true, temporal: true);
        using var fresh = new Stage();
        Assert.Equal(fresh.Frame(Stage.Field, false, silhouette: true, temporal: true), reused);
    }

    static bool IsRim(byte[] pixels, int pixel) => pixels[pixel * 4] > 100
        && pixels[pixel * 4] > pixels[pixel * 4 + 1] * 2 && pixels[pixel * 4] > pixels[pixel * 4 + 2] * 2;

    public sealed partial class Stage : IDisposable
    {
        internal const int W = 256, H = 256;
        internal static readonly InteriorHeightField Field = MakeField(0, -.5f, .5f);
        internal static readonly InteriorHeightField HullField = InteriorHeightFieldBuilder.Build([
            new InteriorVolume { Outline = [new(0, -2), new(.75f, -2), new(.75f, 2), new(0, 2)], FloorY = -2, CeilingY = 2 },
            new InteriorVolume { Outline = [new(1.015625f, -2), new(2, -2), new(2, 2), new(1.015625f, 2)], FloorY = -2, CeilingY = 2 },
        ], new InteriorWindow(new Vector2(-2), .015625f, 256), InteriorEncoding.For(-2, 2));
        TemporalFixture? _fixture;
        MeshHandle _box;
        internal Scene3D Scene => Fixture.Scene;
        TemporalFixture Fixture => _fixture ??= new TemporalFixture(W, H, s =>
        {
            s.Post.Quality.AntiAliasing = AntiAliasing.Off;
            s.Post.Quality.Shadows.Mode = ShadowMode.Off;
            s.Post.BackgroundColor = new Color(.02f, .03f, .04f, 1);
            s.Camera.Azimuth = 0;
            s.Camera.Elevation = 0;
            s.Camera.OrthoSize = 4;
            _box = s.LoadMesh(MeshPrimitives.Box(1));
        });

        internal static Vector2 World(int x, int y, Vector2 jitter = default) =>
            new((x + .5f - jitter.X - W / 2f) * (4f / H), (H / 2f - y - .5f + jitter.Y) * (4f / H));

        internal byte[] Frame(InteriorHeightField? field, bool cut, bool silhouette = false, bool temporal = false,
            MeshOutlineOcclusion occlusion = MeshOutlineOcclusion.SceneDepth, bool model = true)
        {
            Scene.RenderOrigin = Vector3.Zero;
            Scene.Camera.Target = Vector3.Zero;
            Scene.Camera.Azimuth = 0f;
            Scene.Camera.Elevation = 0f;
            Scene.Camera.OrthoSize = 4f;
            Scene.InteriorCut = field;
            Scene.ForceTemporalForTests = temporal;
            // Every capture starts from the same history and jitter phase for exact byte comparisons.
            Scene.CameraCut();
            byte[] pixels = Fixture.Frame((s, _) =>
            {
                var world = Matrix4x4.CreateScale(2);
                if (model) s.Draw(new RigidInstanceDraw(_box, world)
                { Tint = new Color(.1f, .6f, .2f, 1), CutInsideInteriors = cut });
                if (silhouette) s.DrawMeshSilhouette(_box, world, new Color(1, 0, 0, 1), .05f, cut);
                else s.DrawMeshOutlineDissolved(s.BeginMeshOutline(new Color(1, 0, 0, 1), 2, occlusion), _box, world, 0, false, cut);
            });
            Fixture.SkipFrames(TemporalJitter.NativePhaseCount - 1);
            return pixels;
        }

        internal float[] Coverage(string name)
        {
            object renderer = HighlightTestAccess.Field<object>(Scene, "_targetOutlines");
            IGpuTexture source = HighlightTestAccess.Field<IGpuTexture>(renderer, name);
            using var staging = Fixture.Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                source.Width, source.Height, source.Format, GpuTextureUsage.Staging));
            using (var commands = Fixture.Device.Factory.CreateCommandList())
            {
                using (GpuRecording.Open(Fixture.Device, commands, "InteriorCutHighlight.Coverage"))
                    commands.CopyTextureSubresource(source, 0, 0, staging, source.Width, source.Height);
                Fixture.Device.Submit(commands);
                Fixture.Device.WaitForIdle();
            }
            var map = Fixture.Device.Map(staging, GpuMapMode.Read);
            try
            {
                float[] result = new float[W * H];
                byte[] row = new byte[map.RowPitch];
                for (int y = 0; y < H; y++)
                {
                    Marshal.Copy(map.Data + y * (int)map.RowPitch, row, 0, row.Length);
                    for (int x = 0; x < W; x++) result[y * W + x] = source.Format == GpuPixelFormat.R8UNorm
                        ? row[x] / 255f : (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(row, x * 4));
                }
                return result;
            }
            finally { Fixture.Device.Unmap(staging); }
        }

        static InteriorHeightField MakeField(float minX, float floor, float top) => InteriorHeightFieldBuilder.Build([
            new InteriorVolume { Outline = [new(minX, -2), new(2, -2), new(2, 2), new(minX, 2)], FloorY = floor, CeilingY = top },
        ], new InteriorWindow(new Vector2(-2), .25f, 16), InteriorEncoding.For(floor, top));

        public void Dispose() => _fixture?.Dispose();
    }
}
