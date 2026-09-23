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
}
