using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// A static point light's row is rebuilt when, and only when, something its map depends on changed. Headless over
/// the fake device, reading <see cref="ShadowPassDiagnostics.PointStaticRebuilds"/>, so the change detection is
/// pinned on every CI leg rather than only where the GPU suites run.
/// </summary>
public sealed class PointCasterSignatureTests
{
    static readonly Vector3 Light = new(0f, 2f, 0f);
    const float Radius = 6f;

    static void QueueLight(Scene3D scene) => scene.AddLight(Light, Color.White, Radius, 1f, LightShadow.Static(1));

    /// <summary>Frame one asks for the atlas, frame two draws the row once.</summary>
    static void Warm(PointShadowRig rig, System.Action<Scene3D> queue)
    {
        rig.RenderFrame(queue);
        Assert.Equal(1, rig.RenderFrame(queue).PointStaticRebuilds);
    }

    [Fact]
    public void ACasterMovingFarFromTheLightNeverDirtiesItsRow()
    {
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Queue(Scene3D s, float farX)
        {
            s.Draw(box, Matrix4x4.CreateTranslation(1.5f, 0.5f, 0f));
            s.Draw(box, Matrix4x4.CreateTranslation(farX, 0.5f, 0f));
            QueueLight(s);
        }
        Warm(rig, s => Queue(s, 100f));
        for (int frame = 1; frame <= 4; frame++)
        {
            float x = 100f + frame * 3f;
            Assert.Equal(0, rig.RenderFrame(s => Queue(s, x)).PointStaticRebuilds);
        }
    }

    [Fact]
    public void ACasterMovingOneFloatStepInsideTheLightRebuildsItsRow()
    {
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Queue(Scene3D s, float x)
        {
            s.Draw(box, Matrix4x4.CreateTranslation(x, 0.5f, 0f));
            QueueLight(s);
        }
        Warm(rig, s => Queue(s, 1.5f));
        float moved = MathF.BitIncrement(1.5f);
        Assert.Equal(1, rig.RenderFrame(s => Queue(s, moved)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => Queue(s, moved)).PointStaticRebuilds);
    }

    [Fact]
    public void ACasterEnteringAndLeavingTheLightAcrossACellEdgeRebuildsItsRowEachTime()
    {
        // x = 9 is outside the light (9.1 m against a 6.9 m reach) and one cell over from x = 4, which is inside.
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Queue(Scene3D s, float x)
        {
            s.Draw(box, Matrix4x4.CreateTranslation(x, 0.5f, 0f));
            QueueLight(s);
        }
        Warm(rig, s => Queue(s, 9f));
        Assert.Equal(1, rig.RenderFrame(s => Queue(s, 4f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => Queue(s, 4f)).PointStaticRebuilds);
        Assert.Equal(1, rig.RenderFrame(s => Queue(s, 9f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => Queue(s, 9f)).PointStaticRebuilds);
    }

    [Fact]
    public void AnOversizeGroundMovingUnderTheLightRebuildsItsRow()
    {
        // A 40 m plane has a 28 m sphere, wider than a cell, so the index only ever finds it through the oversize
        // list.
        using var rig = new PointShadowRig();
        MeshHandle ground = rig.Scene.LoadMesh(MeshPrimitives.Plane(40f, 40f));
        void Queue(Scene3D s, float y)
        {
            s.Draw(ground, Matrix4x4.CreateTranslation(0f, y, 0f));
            QueueLight(s);
        }
        Warm(rig, s => Queue(s, 0f));
        Assert.Equal(0, rig.RenderFrame(s => Queue(s, 0f)).PointStaticRebuilds);
        Assert.Equal(1, rig.RenderFrame(s => Queue(s, 0.01f)).PointStaticRebuilds);
    }

    [Fact]
    public void AnOptedOutCasterMovingInsideTheLightNeverDirtiesItsRow()
    {
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Queue(Scene3D s, float x)
        {
            s.Draw(box, Matrix4x4.CreateTranslation(1.5f, 0.5f, 0f));
            s.Draw(box, Matrix4x4.CreateTranslation(x, 0.5f, 1f), Color.White, Material.None, castsShadows: false);
            QueueLight(s);
        }
        Warm(rig, s => Queue(s, -1f));
        for (int frame = 1; frame <= 3; frame++)
        {
            float x = -1f + frame * 0.5f;
            Assert.Equal(0, rig.RenderFrame(s => Queue(s, x)).PointStaticRebuilds);
        }
    }

    static void QueueFadingProp(Scene3D scene, MeshHandle box, float threshold, float complement = 0f)
    {
        scene.Draw(box, Matrix4x4.CreateTranslation(1.5f, 0.5f, 0f), Color.White, Material.None,
            threshold, 0.05f, Color.White, true, false, complement);
        QueueLight(scene);
    }

    [Fact]
    public void AFadeThatStaysInsideOneStepDoesNotRebuildTheStaticRow()
    {
        // 0.35 to 0.40 is 5.6 to 6.4 sixteenths, all step 6, so six frames of a fade band leave the map alone.
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, 0.35f));
        for (int frame = 1; frame <= 5; frame++)
        {
            float threshold = 0.35f + frame * 0.01f;
            Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, threshold)).PointStaticRebuilds);
        }
    }

    [Fact]
    public void AFadeCrossingOneStepRebuildsTheStaticRowExactlyOnce()
    {
        // 0.40 is 6.4 sixteenths (step 6) and 0.41 is 6.56 (step 7). The two frames after it stay on step 7.
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, 0.40f));
        Assert.Equal(1, rig.RenderFrame(s => QueueFadingProp(s, box, 0.41f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 0.42f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 0.43f)).PointStaticRebuilds);
    }

    [Fact]
    public void ThresholdsPastOneAreAllTheLastStep()
    {
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, 1.2f));
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 1.4f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 1.0f)).PointStaticRebuilds);
    }

    [Fact]
    public void AComplementFlipAtAConstantThresholdRebuildsTheStaticRow()
    {
        // The dissolve pipeline keeps the opposite noise set once the complement is set, so the map changes while
        // the threshold does not.
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, 0.5f, complement: 0f));
        Assert.Equal(1, rig.RenderFrame(s => QueueFadingProp(s, box, 0.5f, complement: 1f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 0.5f, complement: 1f)).PointStaticRebuilds);
    }

    [Fact]
    public void NegativeThresholdsAreAllTheFirstStep()
    {
        // Complemented, because a caster with neither a positive threshold nor a complement is not dissolving and
        // packs a zero threshold, so its negative value never reaches the signature. The shader clamps it to 0, so
        // -0.3, -0.1 and 0 are one map.
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, -0.3f, complement: 1f));
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, -0.1f, complement: 1f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 0f, complement: 1f)).PointStaticRebuilds);
    }

    [Fact]
    public void TheComplementIsReadAboveOneHalfTheWayTheShaderReadsIt()
    {
        // The shaders keep the complementary set only above 0.5, so 0.4 and 0.5 are one map and 0.6 is the other.
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, 0.5f, complement: 0.4f));
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 0.5f, complement: 0.5f)).PointStaticRebuilds);
        Assert.Equal(1, rig.RenderFrame(s => QueueFadingProp(s, box, 0.5f, complement: 0.6f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 0.5f, complement: 0.6f)).PointStaticRebuilds);
    }
}
