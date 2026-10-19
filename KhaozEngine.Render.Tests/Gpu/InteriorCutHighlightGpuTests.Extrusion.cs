using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class InteriorCutHighlightGpuTests
{
    [GpuFact]
    public void SilhouetteCutsHullBelowInteriorTopWhenTheModelBoundsMiss()
    {
        InteriorHeightField field = InteriorCutShadowTests.Field();
        foreach (bool temporal in new[] { false, true })
        {
            byte[] background = _stage.ExtrusionFrame(null, false, temporal, draw: false);
            byte[] solid = _stage.ExtrusionFrame(null, false, temporal);
            byte[] cut = _stage.ExtrusionFrame(field, true, temporal);
            float jitterY = temporal ? _stage.Scene.CurrentFrameView.JitterPixels.Y : 0f;
            int removed = 0, kept = 0;
            for (int y = 0; y < Stage.H; y++)
                for (int x = 0; x < Stage.W; x++)
                {
                    int pixel = y * Stage.W + x;
                    if (!IsRim(solid, pixel)) continue;
                    float worldY = 3.02f + (Stage.H / 2f - y - .5f + jitterY) * (.12f / Stage.H);
                    if (worldY < 2.999f)
                    {
                        Assert.Equal(background.AsSpan(pixel * 4, 4).ToArray(), cut.AsSpan(pixel * 4, 4).ToArray());
                        removed++;
                    }
                    else if (worldY > 3.001f)
                    {
                        Assert.Equal(solid.AsSpan(pixel * 4, 4).ToArray(), cut.AsSpan(pixel * 4, 4).ToArray());
                        kept++;
                    }
                }
            Assert.True(removed > 50, $"rim probes below top {removed}, temporal {temporal}");
            Assert.True(kept > 50, $"rim probes above top {kept}, temporal {temporal}");
        }
    }

    public sealed partial class Stage
    {
        internal byte[] ExtrusionFrame(InteriorHeightField? field, bool cut, bool temporal, bool draw = true)
        {
            Scene.RenderOrigin = Vector3.Zero;
            Scene.Camera.Target = new Vector3(1, 3.02f, 0);
            Scene.Camera.Azimuth = 0;
            Scene.Camera.Elevation = 0;
            Scene.Camera.OrthoSize = .12f;
            Scene.InteriorCut = field;
            Scene.ForceTemporalForTests = temporal;
            Scene.CameraCut();
            byte[] pixels = Fixture.Frame((s, _) =>
            {
                if (!draw) return;
                Matrix4x4 world = Matrix4x4.CreateScale(.02f) * Matrix4x4.CreateTranslation(1, 3.02f, 0);
                s.Draw(new RigidInstanceDraw(_box, world)
                { Tint = new Color(.1f, .6f, .2f, 1), CutInsideInteriors = cut });
                s.DrawMeshSilhouette(_box, world, new Color(1, 0, 0, 1), .05f, cut);
            });
            Fixture.SkipFrames(TemporalJitter.NativePhaseCount - 1);
            return pixels;
        }
    }
}
