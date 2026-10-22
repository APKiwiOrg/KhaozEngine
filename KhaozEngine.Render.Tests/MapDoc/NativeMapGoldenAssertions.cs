using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

internal readonly record struct NativeMapRoi(int Left, int Top, int Right, int Bottom);

internal static class NativeMapGoldenAssertions
{
    internal static readonly NativeMapRoi FeatureRoi = new(32, 24, 608, 456);
    internal static int Changed(Render3DCapture positive, Render3DCapture control, NativeMapRoi roi, int threshold = 24)
    {
        Assert.Equal(positive.Backend, control.Backend); Assert.Equal(positive.Width, control.Width); Assert.Equal(positive.Height, control.Height);
        int count = 0;
        for (int y = roi.Top; y < roi.Bottom; y++) for (int x = roi.Left; x < roi.Right; x++)
        {
            int p = (y * positive.Width + x) * 4;
            int l1 = Math.Abs(positive.Rgba[p] - control.Rgba[p]) + Math.Abs(positive.Rgba[p + 1] - control.Rgba[p + 1]) +
                Math.Abs(positive.Rgba[p + 2] - control.Rgba[p + 2]);
            if (l1 >= threshold) count++;
        }
        return count;
    }
    internal static int Darkened(Render3DCapture shadow, Render3DCapture noShadow, NativeMapRoi roi)
    {
        int count = 0;
        for (int y = roi.Top; y < roi.Bottom; y++) for (int x = roi.Left; x < roi.Right; x++)
        {
            int p = (y * shadow.Width + x) * 4;
            // Mean RGB darkening of eight 8-bit steps, independent of the committed coarse grid.
            if (noShadow.Rgba[p] + noShadow.Rgba[p + 1] + noShadow.Rgba[p + 2] -
                shadow.Rgba[p] - shadow.Rgba[p + 1] - shadow.Rgba[p + 2] >= 24) count++;
        }
        return count;
    }
    internal static void FeatureVisible(Render3DCapture positive, Render3DCapture omitted, NativeMapRoi? roi = null)
    {
        int changed = Changed(positive, omitted, roi ?? FeatureRoi);
        Assert.True(changed >= 64, $"{positive.Backend}: {changed} ROI pixels differ by RGB L1 >=24/255, expected at least 64.");
    }
    internal static void FoliageAfterBare(Render3DCapture a, Render3DCapture bareA, Render3DCapture b, Render3DCapture bareB)
    {
        int changed = 0;
        var roi = new NativeMapRoi(40, 60, 260, 440);
        for (int y = roi.Top; y < roi.Bottom; y++) for (int x = roi.Left; x < roi.Right; x++)
        {
            int p = (y * a.Width + x) * 4, l1 = 0;
            for (int c = 0; c < 3; c++) l1 += Math.Abs((a.Rgba[p + c] - bareA.Rgba[p + c]) - (b.Rgba[p + c] - bareB.Rgba[p + c]));
            if (l1 >= 24) changed++;
        }
        Assert.True(changed >= 64, $"{a.Backend}: focus changed {changed} foliage pixels after the bare LOD control, expected at least 64.");
    }
    internal static NativeMapRoi GroundRoi(NativeMapGoldenEvidence evidence, float x0, float z0, float x1, float z1)
    {
        var camera = evidence.Framing.Camera;
        Vector2[] corners = new Vector2[4];
        camera.WorldToScreen(new(x0, 0, z0), 640, 480, out corners[0]); camera.WorldToScreen(new(x1, 0, z0), 640, 480, out corners[1]);
        camera.WorldToScreen(new(x0, 0, z1), 640, 480, out corners[2]); camera.WorldToScreen(new(x1, 0, z1), 640, 480, out corners[3]);
        return new(Math.Clamp((int)Math.Ceiling(corners.Min(c => c.X)), 0, 640), Math.Clamp((int)Math.Ceiling(corners.Min(c => c.Y)), 0, 480),
            Math.Clamp((int)Math.Floor(corners.Max(c => c.X)), 0, 640), Math.Clamp((int)Math.Floor(corners.Max(c => c.Y)), 0, 480));
    }
    internal static void FarLightDraw(System.Collections.Generic.IEnumerable<MapDrawRecord> draws, Vector3 origin)
    {
        var draw = Assert.Single(draws, d => d.Resource.Key.OwnerId == "far-light");
        Assert.Equal(MapRenderKind.Light, draw.Resource.Key.Kind); Assert.NotNull(draw.Light);
        var actual = draw.LocalTransform.Translation + new Vector3((float)(draw.Anchor.X - origin.X),
            (float)(draw.Anchor.Y - origin.Y), (float)(draw.Anchor.Z - origin.Z));
        Assert.Equal(FarLightReference(origin), actual);
    }
    internal static void FarLightSubmitted(Scene3D scene, Vector3 origin)
    {
        var lights = (System.Collections.IEnumerable)typeof(Scene3D).GetField("_lights",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(scene)!;
        var light = Assert.Single(lights.Cast<KhaozEngine.Render3D.Rendering.ModelRenderer.PointLightData>());
        Assert.Equal(1, scene.LightCount); Assert.Equal(1, scene.PointLightClusters.SubmittedLightCount);
        Assert.Equal(FarLightReference(origin), Scene3D.AnchoredLightPosition(light, origin));
    }
    static Vector3 FarLightReference(Vector3 origin) => new((float)(32003.125d - origin.X),
        (float)(4d - origin.Y), (float)(-31999.625d - origin.Z));
    internal static void HiddenRoofsCast(NativeMapGoldenEvidence evidence)
    {
        Assert.Equal(NativeMapGoldenScene.Warmup + 1, evidence.Frames.Length);
        foreach (var frame in evidence.Frames)
        {
            var roofs = frame.Submitted.Where(d => d.Resource.Key.Kind == MapRenderKind.Roof).ToArray();
            Assert.Equal(2, roofs.Length); Assert.Equal(2, roofs.Select(d => d.Resource).Distinct().Count());
            Assert.All(roofs, r => Assert.Equal(MapDrawChannels.Shadow | MapDrawChannels.Sky | MapDrawChannels.Physical, r.Channels));
            Assert.Equal(2, frame.ShadowOnlyInstances);
        }
        // Cached frames report zero draw calls. Prove the roof submission on an atlas-recording frame.
        Assert.Contains(evidence.Frames, f => f.Shadow.Active && f.Shadow.Rendered && !f.Shadow.Skipped &&
            f.Shadow.RigidDrawCalls > 0 && f.ShadowOnlyInstances == 2 &&
            f.Submitted.Count(d => d.Resource.Key.Kind == MapRenderKind.Roof && (d.Channels & MapDrawChannels.Shadow) != 0) == 2);
    }
    internal static void OriginPair(Render3DCapture a, NativeMapGoldenEvidence ea, Render3DCapture b, NativeMapGoldenEvidence eb)
    {
        Assert.Equal(ea.SampleKeys, eb.SampleKeys); Assert.NotEmpty(ea.SampleKeys);
        Assert.Equal(ea.Submitted.Select(d => d.Resource.Key), eb.Submitted.Select(d => d.Resource.Key));
        Assert.Equal(ea.ColourInstances, eb.ColourInstances); Assert.Equal(1, ea.Lights); Assert.Equal(1, eb.Lights);
        FarLightDraw(ea.Submitted, new(32000, 0, -32000)); FarLightDraw(eb.Submitted, new(31872, 0, -31872));
        long error = 0;
        for (int i = 0; i < a.Rgba.Length; i++) if (i % 4 != 3) error += Math.Abs(a.Rgba[i] - b.Rgba[i]);
        double mean = error / (255d * a.Width * a.Height * 3);
        Assert.InRange(mean, 0, .01);
    }
    internal static void Complete(NativeMapGoldenEvidence evidence)
    {
        Assert.Equal(0, evidence.Ready.Pending); Assert.True(evidence.Ready.Ready > 0);
        Assert.True(evidence.Ready.Transition.AcceptedWitness!.CompleteForScope);
        Assert.True(evidence.Created > 0); Assert.True(evidence.UploadBytes > 0);
        Assert.True(evidence.ColourInstances > 0); Assert.NotEmpty(evidence.Submitted);
        Assert.Equal(evidence.ExpectedFoliageInstances, evidence.SubmittedFoliageInstances);
        Assert.Equal(evidence.ExpectedFoliagePatches, evidence.SubmittedFoliagePatches);
    }
}
