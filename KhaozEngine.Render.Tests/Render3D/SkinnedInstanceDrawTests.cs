using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="SkinnedInstanceDraw"/> and
/// <see cref="Scene3D.DrawSkinned(in SkinnedInstanceDraw, ReadOnlySpan{Matrix4x4})"/>. Every public skinned overload
/// forwards to the descriptor path with no key. The queued draw and its composed bone palette window, which is all
/// the render loop reads for a skinned draw, must match the hand-built descriptor exactly. Each case also queues
/// through the call the overload made before it forwarded, so a misreading shared by the overload and its descriptor
/// row cannot pass.
/// </summary>
public sealed class SkinnedInstanceDrawTests
{
    static readonly Matrix4x4 Model = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(-220f, 1f, 90f);
    static readonly Color Tint = new(0.6f, 0.8f, 1f, 1f);
    static readonly Material Shiny = new(new Color(0.1f, 0.2f, 0.9f, 1f), 0.5f, 40f);
    static readonly Color Edge = new(1.5f, 0.75f, 0.25f, 1f);

    // Legacy is the queue call the overload's body made before the descriptor path. Every old overload funnelled into
    // the castsShadows dissolve body, which composed the palette with ComposeBonesIntoSlot and called the
    // eight-argument SkinnedSceneInstances.Add with the values the chain resolved.
    sealed record Case(string Signature, Action<Scene3D, SkinnedMeshHandle, Matrix4x4[]> Old,
        Func<SkinnedMeshHandle, SkinnedInstanceDraw> Descriptor, Action<SkinnedSceneInstances, SkinnedMeshHandle> Legacy);

    static readonly Case[] Cases =
    {
        new("DrawSkinned(SkinnedMeshHandle,ReadOnlySpan<Matrix4x4>,Matrix4x4,Color)",
            (s, h, b) => s.DrawSkinned(h, b, Model, Tint),
            h => new SkinnedInstanceDraw(h, Model) { Tint = Tint },
            (q, h) => q.Add(h, Model, Tint, Material.None, 0f, 0f, default, true)),
        new("DrawSkinned(SkinnedMeshHandle,ReadOnlySpan<Matrix4x4>,Matrix4x4,Color,Material)",
            (s, h, b) => s.DrawSkinned(h, b, Model, Tint, Shiny),
            h => new SkinnedInstanceDraw(h, Model) { Tint = Tint, Material = Shiny },
            (q, h) => q.Add(h, Model, Tint, Shiny, 0f, 0f, default, true)),
        new("DrawSkinned(SkinnedMeshHandle,ReadOnlySpan<Matrix4x4>,Matrix4x4,Color,Material,Single,Single,Color)",
            (s, h, b) => s.DrawSkinned(h, b, Model, Tint, Shiny, 0.45f, 0.12f, Edge),
            h => new SkinnedInstanceDraw(h, Model)
            {
                Tint = Tint, Material = Shiny, Dissolve = 0.45f, DissolveEdgeWidth = 0.12f, DissolveEdgeColor = Edge,
            },
            (q, h) => q.Add(h, Model, Tint, Shiny, 0.45f, 0.12f, Edge, true)),
        new("DrawSkinned(SkinnedMeshHandle,ReadOnlySpan<Matrix4x4>,Matrix4x4,Color,Material,Boolean)",
            (s, h, b) => s.DrawSkinned(h, b, Model, Tint, Shiny, false),
            h => new SkinnedInstanceDraw(h, Model) { Tint = Tint, Material = Shiny, CastsShadows = false },
            (q, h) => q.Add(h, Model, Tint, Shiny, 0f, 0f, default, false)),
        new("DrawSkinned(SkinnedMeshHandle,ReadOnlySpan<Matrix4x4>,Matrix4x4,Color,Material,Single,Single,Color,Boolean)",
            (s, h, b) => s.DrawSkinned(h, b, Model, Tint, Shiny, 0.45f, 0.12f, Edge, false),
            h => new SkinnedInstanceDraw(h, Model)
            {
                Tint = Tint, Material = Shiny, Dissolve = 0.45f, DissolveEdgeWidth = 0.12f, DissolveEdgeColor = Edge,
                CastsShadows = false,
            },
            (q, h) => q.Add(h, Model, Tint, Shiny, 0.45f, 0.12f, Edge, false)),
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
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        Matrix4x4[] bones = MotionTestScene.Bent(mesh, 0.35f);

        scene.Begin();
        c.Old(scene, tube, bones);
        SkinnedSceneInstances.Instance[] viaOverload = scene.QueuedSkinnedInstancesForTests.ToArray();
        Matrix4x4[] paletteViaOverload = scene.BonePaletteSlotForTests(0);

        scene.Begin();
        scene.DrawSkinned(c.Descriptor(tube), bones);
        SkinnedSceneInstances.Instance[] viaDescriptor = scene.QueuedSkinnedInstancesForTests.ToArray();
        Matrix4x4[] paletteViaDescriptor = scene.BonePaletteSlotForTests(0);

        var legacyQueue = new SkinnedSceneInstances();
        c.Legacy(legacyQueue, tube);
        SkinnedSceneInstances.Instance[] viaLegacy = legacyQueue.Items.ToArray();
        var legacyPalette = new List<Matrix4x4>();
        Scene3D.ComposeBonesIntoSlot(legacyPalette, 0, bones, mesh.InverseBind);
        Matrix4x4[] paletteViaLegacy = legacyPalette.ToArray();

        Assert.Single(viaOverload);
        Assert.True(viaOverload[0].Motion.IsNone);
        AssertSameInstances(viaLegacy, viaOverload);
        AssertSameInstances(viaLegacy, viaDescriptor);
        Assert.Equal(paletteViaLegacy, paletteViaOverload);
        Assert.Equal(paletteViaLegacy, paletteViaDescriptor);
    }

    [Fact]
    public void Every_public_skinned_overload_has_a_case()
    {
        string[] overloads = typeof(Scene3D).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "DrawSkinned")
            .Where(m => m.GetParameters() is { Length: > 0 } p && p[0].ParameterType == typeof(SkinnedMeshHandle))
            .Select(Signature)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Cases.Select(c => c.Signature).OrderBy(s => s, StringComparer.Ordinal), overloads);
    }

    [Fact]
    public void The_constructor_carries_the_plain_overloads_defaults()
    {
        var draw = new SkinnedInstanceDraw(new SkinnedMeshHandle(2, 1), Model);
        Assert.Equal(Model, draw.Model);
        Assert.Equal(Color.White, draw.Tint);
        Assert.Equal(Material.None.Shininess, draw.Material.Shininess);
        Assert.Equal(Material.None.Emissive, draw.Material.Emissive);
        Assert.Equal(0f, draw.Dissolve);
        Assert.Equal(0f, draw.DissolveEdgeWidth);
        Assert.Equal(default(Color), draw.DissolveEdgeColor);
        Assert.True(draw.CastsShadows);
        Assert.True(draw.Motion.IsNone);
    }

    [Fact]
    public void A_key_rides_the_queue_and_leaves_the_palette_unchanged()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        Matrix4x4[] bones = MotionTestScene.Bent(mesh, 0.2f);
        MotionKey key = MotionKey.From(31);

        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, Model) { Tint = Tint }, bones);
        Matrix4x4[] unkeyed = scene.BonePaletteSlotForTests(0);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, Model) { Tint = Tint, Motion = key }, bones);

        Assert.Equal(key, Assert.Single(scene.QueuedSkinnedInstancesForTests).Motion);
        Assert.Equal(unkeyed, scene.BonePaletteSlotForTests(0));
    }

    [Fact]
    public void A_stale_or_default_handle_queues_nothing_through_either_path()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        scene.UnloadSkinnedMesh(tube);

        scene.Begin();
        scene.DrawSkinned(tube, mesh.RestPose, Model, Tint);
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, Model) { Motion = MotionKey.From(3) }, mesh.RestPose);
        scene.DrawSkinned(new SkinnedInstanceDraw(default, Model), mesh.RestPose);

        Assert.Empty(scene.QueuedSkinnedInstancesForTests);
    }

    [Fact]
    public void A_wrong_bone_count_is_refused_the_same_way_through_either_path()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        Matrix4x4[] tooFew = mesh.RestPose[..2];

        scene.Begin();
        ArgumentException viaOverload = Assert.Throws<ArgumentException>(
            () => scene.DrawSkinned(tube, tooFew, Model, Tint));
        ArgumentException viaDescriptor = Assert.Throws<ArgumentException>(
            () => scene.DrawSkinned(new SkinnedInstanceDraw(tube, Model), tooFew));

        Assert.Equal(viaOverload.Message, viaDescriptor.Message);
        Assert.Empty(scene.QueuedSkinnedInstancesForTests);
    }

    static string Signature(MethodInfo m)
        => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => TypeName(p.ParameterType)))})";

    static string TypeName(Type t) => t.IsGenericType
        ? $"{t.Name[..t.Name.IndexOf('`')]}<{string.Join(",", t.GetGenericArguments().Select(TypeName))}>"
        : t.Name;

    // Every public field, the new Motion included, plus the derived Dissolving flag the pipeline choice reads.
    static void AssertSameInstances(SkinnedSceneInstances.Instance[] expected, SkinnedSceneInstances.Instance[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        FieldInfo[] fields = typeof(SkinnedSceneInstances.Instance).GetFields(BindingFlags.Public | BindingFlags.Instance);
        Assert.Contains(fields, f => f.Name == nameof(SkinnedSceneInstances.Instance.Motion));
        for (int i = 0; i < expected.Length; i++)
        {
            foreach (FieldInfo f in fields)
                Assert.True(Equals(f.GetValue(expected[i]), f.GetValue(actual[i])),
                    $"skinned instance {i} differs in {f.Name}");
            Assert.Equal(expected[i].Dissolving, actual[i].Dissolving);
        }
    }
}
