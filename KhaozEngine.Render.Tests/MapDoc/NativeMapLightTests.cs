using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapLightTests
{
    [Fact]
    public void FullEmitterRoster_UsesEffectiveTransformsWithoutNearestCap()
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = NativeMapPlacementTests.Scene(closure);
        var recorder = new MapDrawRecorder(); NativeMapLights.Record(scene, NativeMapPlacementTests.Frame, recorder);
        var set = recorder.Snapshot();
        Assert.Equal(17, set.Draws.Select(d => d.Resource.Key.OwnerId).Distinct().Count());
        Assert.Equal(MapEffectiveAnnotations.Lights(scene), set.Resources.Select(r => Assert.IsType<MapLightResource>(r.Payload).Light));
        for (int i = 0; i < 17; i++)
        {
            var draw = set.Draws[i]; var light = draw.Light!.Value;
            Assert.Equal("leaf-a", light.FixtureOwnerId); Assert.Equal(new Color(1f, .25f, .5f), light.Colour);
            Assert.Equal(2 + i, light.Intensity); Assert.Equal(2.7f, light.Radius);
            Assert.Equal(i % 2 == 0, light.Shadow.Requested);
            if (light.Shadow.Requested) Assert.True(light.Shadow.HasExclusionBox);
            double ownerX = -1.25 + Math.Cos(.371f) * (.125 * 1.2f) + Math.Sin(.371f) * (-.375 * 1.2f);
            double ownerZ = 3.75 - Math.Sin(.371f) * (.125 * 1.2f) + Math.Cos(.371f) * (-.375 * 1.2f);
            double yaw = (double).371f + .2f, scale = (double)1.2f * .75f;
            double x = ownerX + Math.Cos(yaw) * (i * .125 * scale) + Math.Sin(yaw) * (-.25 * scale);
            double z = ownerZ - Math.Sin(yaw) * (i * .125 * scale) + Math.Cos(yaw) * (-.25 * scale);
            Assert.InRange(Math.Abs(draw.Anchor.X + (double)draw.LocalTransform.M41 - x), 0, .000001);
            Assert.InRange(Math.Abs(draw.Anchor.Y + (double)draw.LocalTransform.M42 - 3.2500000298023224), 0, .000001);
            Assert.InRange(Math.Abs(draw.Anchor.Z + (double)draw.LocalTransform.M43 - z), 0, .000001);
        }
    }

    [Fact]
    public void AnchoredLights_ReduceAtReceiverAndPointShadowBinding()
    {
        using var harness = new MotionTestScene(); var scene = harness.Scene;
        var local = new Vector3(.0001f, .234567f, -.345678f); var anchor = new RenderAnchor(32000, -500, -32000);
        scene.Begin(); scene.AddLightAnchored(local, anchor, Color.White, 4, 2, LightShadow.Static(9));
        var lights = (List<ModelRenderer.PointLightData>)typeof(Scene3D).GetField("_lights",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(scene)!;
        var output = new ModelRenderer.PointLightGpuData[1];
        ModelRenderer.BuildPointLightRecords(lights.ToArray(), output, [], [], 0, 0, new(32000, -500, -32000));
        Assert.Equal(new Vector4(local, 4), output[0].PosRadius);
        Assert.Equal(local, Scene3D.AnchoredLightPosition(lights[0], new(32000, -500, -32000)));
    }
}
