using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="RigidInstanceDraw"/> and <see cref="Scene3D.Draw(in RigidInstanceDraw)"/> (TEMPORAL-FOUNDATIONS-DESIGN
/// section 3). Every public rigid overload forwards to the descriptor path with no key. What it queues, and the
/// instance stream grouping packs from that queue, must match the hand-built descriptor byte for byte. The coverage
/// test holds the case list to the real overload set, so an overload added later without a case fails here.
/// </summary>
public sealed class RigidInstanceDrawTests
{
    static readonly MeshHandle Mesh = new(3, 2);
    static readonly MeshHandle Other = new(4, 1);
    static readonly Matrix4x4 World = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateRotationY(0.4f)
        * Matrix4x4.CreateTranslation(300f, 2f, -140f);
    static readonly Color Tint = new(0.9f, 0.5f, 0.25f, 0.75f);
    static readonly Material Shiny = new(new Color(0.2f, 0.4f, 1.5f, 1f), 0.6f, 48f);
    static readonly Color Edge = new(2f, 1.25f, 0.5f, 1f);

    // One row per public rigid overload: the call through its own signature, and the descriptors that must queue
    // exactly the same. Signature spells the CLR parameter types, matching Every_public_rigid_overload_has_a_case.
    sealed record Case(string Signature, Action<Scene3D> Old, RigidInstanceDraw[] Descriptors);

    static readonly Case[] Cases =
    {
        new("Draw(MeshHandle,Matrix4x4)", s => s.Draw(Mesh, World),
            new[] { new RigidInstanceDraw(Mesh, World) }),
        new("Draw(MeshHandle,Matrix4x4,Color)", s => s.Draw(Mesh, World, Tint),
            new[] { new RigidInstanceDraw(Mesh, World) { Tint = Tint } }),
        new("Draw(MeshHandle,Matrix4x4,Color,Material)", s => s.Draw(Mesh, World, Tint, Shiny),
            new[] { new RigidInstanceDraw(Mesh, World) { Tint = Tint, Material = Shiny } }),
        new("Draw(MeshHandle,Matrix4x4,Color,Material,Single,Single,Color)",
            s => s.Draw(Mesh, World, Tint, Shiny, 0.4f, 0.1f, Edge),
            new[]
            {
                new RigidInstanceDraw(Mesh, World)
                {
                    Tint = Tint, Material = Shiny, Dissolve = 0.4f, DissolveEdgeWidth = 0.1f, DissolveEdgeColor = Edge,
                },
            }),
        new("Draw(MeshHandle,Matrix4x4,Color,Material,Boolean)", s => s.Draw(Mesh, World, Tint, Shiny, false),
            new[] { new RigidInstanceDraw(Mesh, World) { Tint = Tint, Material = Shiny, CastsShadows = false } }),
        new("Draw(MeshHandle,Matrix4x4,Color,Material,Single,Single,Color,Boolean)",
            s => s.Draw(Mesh, World, Tint, Shiny, 0.4f, 0.1f, Edge, false),
            new[]
            {
                new RigidInstanceDraw(Mesh, World)
                {
                    Tint = Tint, Material = Shiny, Dissolve = 0.4f, DissolveEdgeWidth = 0.1f, DissolveEdgeColor = Edge,
                    CastsShadows = false,
                },
            }),
        new("Draw(MeshHandle,Matrix4x4,Color,Material,Single,Single,Color,Boolean,Boolean)",
            s => s.Draw(Mesh, World, Tint, Shiny, 0.4f, 0.1f, Edge, true, true),
            new[]
            {
                new RigidInstanceDraw(Mesh, World)
                {
                    Tint = Tint, Material = Shiny, Dissolve = 0.4f, DissolveEdgeWidth = 0.1f, DissolveEdgeColor = Edge,
                    InvertShadowDissolve = true,
                },
            }),
        new("Draw(MeshHandle,Matrix4x4,Color,Material,Single,Single,Color,Boolean,Boolean,Single)",
            s => s.Draw(Mesh, World, Tint, Shiny, 0.4f, 0.1f, Edge, true, false, 1f),
            new[]
            {
                new RigidInstanceDraw(Mesh, World)
                {
                    Tint = Tint, Material = Shiny, Dissolve = 0.4f, DissolveEdgeWidth = 0.1f, DissolveEdgeColor = Edge,
                    DissolveComplement = 1f,
                },
            }),
        new("DrawShadowOnly(MeshHandle,Matrix4x4)", s => s.DrawShadowOnly(Mesh, World),
            new[] { new RigidInstanceDraw(Mesh, World) { ShadowOnly = true } }),
        new("Draw(PropHandle,Matrix4x4)", s => s.Draw(new Scene3D.PropHandle(new[] { Mesh, Other }), World),
            new[] { new RigidInstanceDraw(Mesh, World), new RigidInstanceDraw(Other, World) }),
        new("Draw(PropHandle,Matrix4x4,Color)",
            s => s.Draw(new Scene3D.PropHandle(new[] { Mesh, Other }), World, Tint),
            new[] { new RigidInstanceDraw(Mesh, World) { Tint = Tint }, new RigidInstanceDraw(Other, World) { Tint = Tint } }),
    };

    public static TheoryData<string> Signatures()
    {
        var data = new TheoryData<string>();
        foreach (Case c in Cases) data.Add(c.Signature);
        return data;
    }

    [Theory]
    [MemberData(nameof(Signatures))]
    public void Each_overload_queues_exactly_what_its_descriptor_queues(string signature)
    {
        Case c = Cases.Single(x => x.Signature == signature);
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;

        scene.Begin();
        c.Old(scene);
        SceneInstances.Instance[] viaOverload = scene.QueuedInstancesForTests.ToArray();

        scene.Begin();
        foreach (RigidInstanceDraw d in c.Descriptors) scene.Draw(d);
        SceneInstances.Instance[] viaDescriptor = scene.QueuedInstancesForTests.ToArray();

        Assert.NotEmpty(viaOverload);
        Assert.All(viaOverload, i => Assert.True(i.Motion.IsNone));
        AssertSameInstances(viaOverload, viaDescriptor);
        AssertSamePacking(viaOverload, viaDescriptor);
    }

    [Fact]
    public void Every_public_rigid_overload_has_a_case()
    {
        string[] overloads = typeof(Scene3D).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name is "Draw" or "DrawShadowOnly")
            .Where(m => m.GetParameters() is { Length: > 0 } p
                && (p[0].ParameterType == typeof(MeshHandle) || p[0].ParameterType == typeof(Scene3D.PropHandle)))
            .Select(Signature)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Cases.Select(c => c.Signature).OrderBy(s => s, StringComparer.Ordinal), overloads);
    }

    [Fact]
    public void The_constructor_carries_the_plain_overloads_defaults()
    {
        var draw = new RigidInstanceDraw(Mesh, World);
        Assert.Equal(Mesh, draw.Mesh);
        Assert.Equal(World, draw.World);
        Assert.Equal(Color.White, draw.Tint);
        Assert.Equal(Material.None.Emissive, draw.Material.Emissive);
        Assert.Equal(Material.None.Specular, draw.Material.Specular);
        Assert.Equal(Material.None.Shininess, draw.Material.Shininess);
        Assert.Equal(0f, draw.Dissolve);
        Assert.Equal(0f, draw.DissolveEdgeWidth);
        Assert.Equal(default(Color), draw.DissolveEdgeColor);
        Assert.True(draw.CastsShadows);
        Assert.False(draw.ShadowOnly);
        Assert.False(draw.InvertShadowDissolve);
        Assert.Equal(0f, draw.DissolveComplement);
        Assert.True(draw.Motion.IsNone);
    }

    [Fact]
    public void A_key_rides_the_queue_and_leaves_the_packed_stream_unchanged()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        MotionKey key = MotionKey.Combine(MotionKey.From(77), 2);

        scene.Begin();
        scene.Draw(new RigidInstanceDraw(Mesh, World) { Tint = Tint, Dissolve = 0.3f });
        SceneInstances.Instance[] unkeyed = scene.QueuedInstancesForTests.ToArray();
        scene.Begin();
        scene.Draw(new RigidInstanceDraw(Mesh, World) { Tint = Tint, Dissolve = 0.3f, Motion = key });
        SceneInstances.Instance[] keyed = scene.QueuedInstancesForTests.ToArray();

        Assert.Equal(key, Assert.Single(keyed).Motion);
        AssertSamePacking(unkeyed, keyed);
    }

    [Fact]
    public void The_queue_takes_a_descriptor_directly()
    {
        var queue = new SceneInstances();
        queue.Add(new RigidInstanceDraw(Mesh, World) { Motion = MotionKey.From(9) });
        Assert.Equal(MotionKey.From(9), Assert.Single(queue.Items).Motion);
    }

    [Fact]
    public void Shadow_only_without_casting_is_refused_as_the_instance_constructor_refuses_it()
    {
        ArgumentException expected = Assert.Throws<ArgumentException>(() =>
        {
            _ = new SceneInstances.Instance(Mesh, World, Color.White, Material.None, castsShadows: false,
                shadowOnly: true);
        });
        using var harness = new MotionTestScene();
        harness.Scene.Begin();
        ArgumentException actual = Assert.Throws<ArgumentException>(() =>
            harness.Scene.Draw(new RigidInstanceDraw(Mesh, World) { ShadowOnly = true, CastsShadows = false }));

        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.ParamName, actual.ParamName);
        Assert.Empty(harness.Scene.QueuedInstancesForTests);
    }

    static string Signature(MethodInfo m)
        => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})";

    // Every public property, the new Motion included, so a knob added later is compared without editing this test.
    static void AssertSameInstances(SceneInstances.Instance[] expected, SceneInstances.Instance[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        PropertyInfo[] properties = typeof(SceneInstances.Instance)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Contains(properties, p => p.Name == nameof(SceneInstances.Instance.Motion));
        for (int i = 0; i < expected.Length; i++)
            foreach (PropertyInfo p in properties)
                Assert.True(Equals(p.GetValue(expected[i]), p.GetValue(actual[i])),
                    $"instance {i} differs in {p.Name}");
    }

    // What the renderer uploads for these instances (the packed stream's raw bytes) and the CPU-side slot lists
    // grouping derives from them (runs, caster kinds, shadow-only mask).
    static void AssertSamePacking(SceneInstances.Instance[] expected, SceneInstances.Instance[] actual)
    {
        Pack(expected, out byte[] expectedBytes, out Scene3D.MeshRun[] expectedRuns,
            out ShadowCastKind[] expectedKinds, out bool[] expectedShadowOnly);
        Pack(actual, out byte[] actualBytes, out Scene3D.MeshRun[] actualRuns,
            out ShadowCastKind[] actualKinds, out bool[] actualShadowOnly);
        Assert.Equal(expectedBytes, actualBytes);
        Assert.Equal(expectedRuns, actualRuns);
        Assert.Equal(expectedKinds, actualKinds);
        Assert.Equal(expectedShadowOnly, actualShadowOnly);
    }

    static void Pack(SceneInstances.Instance[] items, out byte[] bytes, out Scene3D.MeshRun[] runs,
        out ShadowCastKind[] kinds, out bool[] shadowOnly)
    {
        var data = new List<ModelRenderer.InstanceData>();
        var runList = new List<Scene3D.MeshRun>();
        var kindList = new List<ShadowCastKind>();
        var shadowOnlyList = new List<bool>();
        Scene3D.GroupInstances(items, data, runList, castKinds: kindList, shadowOnly: shadowOnlyList);
        bytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data)).ToArray();
        runs = runList.ToArray();
        kinds = kindList.ToArray();
        shadowOnly = shadowOnlyList.ToArray();
    }
}
