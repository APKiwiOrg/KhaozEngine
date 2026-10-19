using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Real frame recording proves flag propagation and packing on both skinning paths. Task 8 owns
/// the hosted pixel proof, including complementary coverage at the two threshold endpoints.</summary>
public sealed class SkinnedDrawFlagTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Descriptor_preserves_all_skinned_flags(bool gpuSkinning)
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        scene.UseGpuSkinning = gpuSkinning;
        SkinnedMeshHandle mesh = rig.LoadTube(out SkinnedGltfMesh tube);
        var defaults = new SkinnedInstanceDraw(mesh, Matrix4x4.Identity);
        Assert.False(defaults.ShadowOnly);
        Assert.False(defaults.InvertShadowDissolve);
        Assert.Equal(0f, defaults.DissolveComplement);

        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(mesh, Matrix4x4.Identity)
        {
            ShadowOnly = true,
            InvertShadowDissolve = true,
            DissolveComplement = 1f,
        }, tube.RestPose);
        SkinnedSceneInstances.Instance queued = Assert.Single(scene.QueuedSkinnedInstancesForTests);
        Assert.True(queued.ShadowOnly);
        Assert.True(queued.InvertShadowDissolve);
        Assert.Equal(1f, queued.DissolveComplement);
        Assert.True(queued.Dissolving);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shadow_only_without_casting_is_refused_before_palette_queue_or_motion_mutation(bool gpuSkinning)
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        scene.UseGpuSkinning = gpuSkinning;
        scene.ForceTemporalForTests = true;
        SkinnedMeshHandle mesh = rig.LoadTube(out SkinnedGltfMesh tube);
        Matrix4x4[] bones = MotionTestScene.Bent(tube, 0.4f);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(mesh, Matrix4x4.Identity), bones);
        Matrix4x4[] beforePalette = scene.BonePaletteSlotForTests(0);
        var shadowOnlyWithoutCasting = new SkinnedInstanceDraw(mesh, Matrix4x4.Identity)
        {
            ShadowOnly = true,
            CastsShadows = false,
            Motion = MotionKey.From(17),
        };

        Assert.Throws<ArgumentException>(() => scene.DrawSkinned(shadowOnlyWithoutCasting, tube.RestPose));
        Assert.Equal(beforePalette, scene.BonePaletteSlotForTests(0));
        Assert.Single(scene.QueuedSkinnedInstancesForTests);
        Assert.Equal(0, scene.MotionKeyCounts.KeyedSkinned);
        Assert.Throws<ArgumentException>(() => scene.BonePaletteSlotForTests(1));
        scene.Begin();
        Assert.Throws<ArgumentException>(() => scene.DrawSkinned(shadowOnlyWithoutCasting, tube.RestPose));
        Assert.Empty(scene.QueuedSkinnedInstancesForTests);
        Assert.Throws<ArgumentException>(() => scene.BonePaletteSlotForTests(0));
        var queue = new SkinnedSceneInstances();
        Assert.Throws<ArgumentException>(() => queue.Add(shadowOnlyWithoutCasting));
        Assert.Empty(queue.Items);
    }

    public static TheoryData<bool, float, float, bool, bool> FlagRows()
    {
        var rows = new TheoryData<bool, float, float, bool, bool>();
        foreach (bool gpu in new[] { false, true })
            foreach (float threshold in new[] { 0f, .5f, 1f })
                foreach (float complement in new[] { 0f, 1f })
                    foreach (bool inverted in new[] { false, true })
                        foreach (bool casting in new[] { false, true })
                            rows.Add(gpu, threshold, complement, inverted, casting);
        return rows;
    }

    [Theory]
    [MemberData(nameof(FlagRows))]
    public void Complement_endpoints_and_shadow_only_match_rigid_policy(bool gpuSkinning, float threshold,
        float complement, bool inverted, bool casting)
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        ConfigureShadows(scene, gpuSkinning);
        PrimePointShadowAtlas(rig);
        SkinnedMeshHandle mesh = rig.LoadTube(out SkinnedGltfMesh tube);
        var draw = new SkinnedInstanceDraw(mesh, Matrix4x4.Identity)
        {
            Dissolve = threshold,
            DissolveEdgeWidth = .125f,
            DissolveEdgeColor = Color.White,
            DissolveComplement = complement,
            InvertShadowDissolve = inverted,
            CastsShadows = casting,
        };
        scene.Begin();
        scene.DrawSkinned(draw, tube.RestPose);
        scene.AddLight(new Vector3(0f, 1f, 1f), Color.White, 10f, 1f, LightShadow.Dynamic);
        var rigidQueue = new SceneInstances();
        rigidQueue.Add(new RigidInstanceDraw(default, Matrix4x4.Identity)
        {
            Dissolve = threshold,
            DissolveComplement = complement,
            InvertShadowDissolve = inverted,
            CastsShadows = casting,
        });
        ShadowCastKind rigidClassification = Scene3D.ClassifyCaster(Assert.Single(rigidQueue.Items));
        ShadowCastKind skinnedClassification = ShadowDepthSelection.ClassifySkinnedCaster(
            Assert.Single(scene.QueuedSkinnedInstancesForTests));
        bool dissolving = threshold > 0f || complement > 0f;
        ShadowCastKind expected = !casting ? ShadowCastKind.None : !dissolving ? ShadowCastKind.Opaque
            : inverted ? ShadowCastKind.DissolvingInverted : ShadowCastKind.Dissolving;
        Assert.Equal(expected, skinnedClassification);
        Assert.Equal(rigidClassification, skinnedClassification);

        using RecordingGpuCommandList commands = Record(rig);
        string mainFragment = gpuSkinning
            ? dissolving ? ShaderSources.SkinnedModelDissolveFrag : ShaderSources.SkinnedModelFrag
            : dissolving ? ShaderSources.ModelDissolveFrag : ShaderSources.ModelFrag;
        RecordingGpuCommandList.DrawBindings main = Assert.Single(commands.Bindings, b => Fragment(b) == mainFragment);
        Assert.Equal(1, scene.DrawnSkinnedInstances);
        if (gpuSkinning)
        {
            ReadOnlySpan<byte> header = UniformBytes(commands, main, 0);
            Assert.Equal(1f, MemoryMarshal.Read<float>(header.Slice(112))); // dynamic mask stays independent
            Assert.Equal(complement, MemoryMarshal.Read<float>(header.Slice(116)));
            Assert.Equal(threshold, MemoryMarshal.Read<float>(header.Slice(120)));
            Assert.Equal(dissolving ? .125f : 0f, MemoryMarshal.Read<float>(header.Slice(124)));
        }
        else
        {
            ModelRenderer.InstanceData instance = CpuInstance(commands, main);
            Assert.Equal(complement, instance.DissolveComplement);
            Assert.Equal(threshold, instance.Dissolve.X);
            Assert.Equal(dissolving ? .125f : 0f, instance.Dissolve.Y);
        }

        Assert.Equal(casting ? scene.LastShadowPassDiagnostics.CascadeCount : 0,
            scene.LastShadowPassDiagnostics.SkinnedDrawCalls);
        Assert.Equal(casting ? 6 : 0, scene.LastShadowPassDiagnostics.PointDynamicSkinnedDrawCalls);
        if (!casting) return;
        string cascadeFragment = !dissolving ? ShaderSources.ShadowDepthFrag
            : gpuSkinning ? inverted ? ShaderSources.SkinnedShadowDepthDissolveInvertedFrag : ShaderSources.SkinnedShadowDepthDissolveFrag
            : inverted ? ShaderSources.ShadowDepthDissolveInvertedFrag : ShaderSources.ShadowDepthDissolveFrag;
        RecordingGpuCommandList.DrawBindings[] cascades = commands.Bindings.Where(b => Fragment(b) == cascadeFragment).ToArray();
        Assert.Equal(scene.LastShadowPassDiagnostics.CascadeCount, cascades.Length);
        foreach (RecordingGpuCommandList.DrawBindings cascade in cascades)
        {
            if (gpuSkinning && dissolving)
            {
                ReadOnlySpan<byte> depth = UniformBytes(commands, cascade, 0);
                Vector4 parameters = MemoryMarshal.Read<Vector4>(depth.Slice(ShadowMapRenderer.SkinnedDissolveParamsOffset));
                Assert.Equal(threshold, parameters.Y);
                Assert.Equal(complement, parameters.Z);
            }
            else if (!gpuSkinning) Assert.Equal(complement, CpuInstance(commands, cascade).DissolveComplement);
        }
        string pointFragment = !dissolving ? ShaderSources.PointShadowRigidFrag
            : gpuSkinning ? inverted ? ShaderSources.PointShadowSkinnedDissolveInvertedFrag : ShaderSources.PointShadowSkinnedDissolveFrag
            : inverted ? ShaderSources.PointShadowRigidDissolveInvertedFrag : ShaderSources.PointShadowRigidDissolveFrag;
        RecordingGpuCommandList.DrawBindings[] faces = commands.Bindings.Where(b => Fragment(b) == pointFragment).ToArray();
        Assert.Equal(6, faces.Length);
        foreach (RecordingGpuCommandList.DrawBindings face in faces)
        {
            if (gpuSkinning)
            {
                ReadOnlySpan<byte> point = UniformBytes(commands, face, 1);
                Assert.Equal(threshold, MemoryMarshal.Read<float>(point.Slice(64)));
                Assert.Equal(complement, MemoryMarshal.Read<float>(point.Slice(68)));
            }
            else Assert.Equal(complement, CpuInstance(commands, face).DissolveComplement);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Shadow_only_suppresses_main_draws_and_motion_but_retains_both_shadow_families(bool gpuSkinning,
        bool inverted)
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        ConfigureShadows(scene, gpuSkinning);
        PrimePointShadowAtlas(rig);
        scene.ForceTemporalForTests = true;
        scene.FrustumCulling = false; // shadow-only still suppresses colour when camera culling is off
        SkinnedMeshHandle mesh = rig.LoadTube(out SkinnedGltfMesh tube);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(mesh, Matrix4x4.Identity)
        {
            ShadowOnly = true,
            InvertShadowDissolve = inverted,
            DissolveComplement = 1f,
            Motion = MotionKey.From(21),
        }, tube.RestPose);
        scene.AddLight(new Vector3(0f, 1f, 1f), Color.White, 10f, 1f, LightShadow.Dynamic);
        Assert.Equal(0, scene.MotionKeyCounts.KeyedSkinned);
        using RecordingGpuCommandList commands = Record(rig);
        Assert.Equal(0, scene.DrawnSkinnedInstances);
        Assert.Equal(0, scene.CulledSkinnedInstances);
        Assert.Equal(0, commands.Bindings.Count(b => IsMainFragment(Fragment(b))));
        Assert.Equal(scene.LastShadowPassDiagnostics.CascadeCount, scene.LastShadowPassDiagnostics.SkinnedDrawCalls);
        Assert.True(scene.LastShadowPassDiagnostics.SkinnedDrawCalls > 0);
        Assert.Equal(6, scene.LastShadowPassDiagnostics.PointDynamicSkinnedDrawCalls);
        scene.Begin();
        Assert.False(scene.ActiveMotionHistory!.TryGetPreviousSkinned(MotionKey.From(21), out _, out _));
    }

    static void ConfigureShadows(Scene3D scene, bool gpuSkinning)
    {
        scene.UseGpuSkinning = gpuSkinning;
        scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        scene.Post.LightDirection = new Vector3(-.55f, -.8f, -.25f);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Shadow_only_is_retained_for_point_shadows_with_key_shadows_off(bool gpuSkinning, bool staticLight)
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        scene.UseGpuSkinning = gpuSkinning;
        PrimePointShadowAtlas(rig);
        SkinnedMeshHandle mesh = rig.LoadTube(out SkinnedGltfMesh tube);
        scene.Begin();
        var draw = new SkinnedInstanceDraw(mesh, Matrix4x4.Identity)
        {
            ShadowOnly = true,
            DissolveComplement = 1f,
        };
        scene.DrawSkinned(draw, tube.RestPose);
        scene.AddLight(new Vector3(0f, 1f, 1f), Color.White, 10f, 1f,
            staticLight ? LightShadow.Static(7) : LightShadow.Dynamic);
        if (staticLight)
        {
            // Current-pose static shadows need another boundary to allocate the transient atlas after demand.
            using RecordingGpuCommandList warmup = Record(rig);
            Assert.Equal(1, scene.LastShadowPassDiagnostics.PointTransientDemand);
            scene.Begin();
            scene.DrawSkinned(draw, tube.RestPose);
            scene.AddLight(new Vector3(0f, 1f, 1f), Color.White, 10f, 1f, LightShadow.Static(7));
        }
        using RecordingGpuCommandList commands = Record(rig);
        Assert.Equal(0, scene.DrawnSkinnedInstances);
        Assert.Equal(0, scene.LastShadowPassDiagnostics.SkinnedDrawCalls);
        Assert.Equal(staticLight ? 6 : 0, scene.LastShadowPassDiagnostics.PointStaticTransientSkinnedDrawCalls);
        Assert.Equal(staticLight ? 0 : 6, scene.LastShadowPassDiagnostics.PointDynamicSkinnedDrawCalls);
        Assert.DoesNotContain(commands.Bindings, b => IsMainFragment(Fragment(b)));
        RecordingGpuCommandList.DrawBindings[] faces = commands.Bindings
            .Where(b => Fragment(b) == (gpuSkinning ? ShaderSources.PointShadowSkinnedDissolveFrag
                : ShaderSources.PointShadowRigidDissolveFrag)).ToArray();
        Assert.Equal(6, faces.Length);
        foreach (RecordingGpuCommandList.DrawBindings face in faces)
        {
            float complement = gpuSkinning
                ? MemoryMarshal.Read<float>(UniformBytes(commands, face, 1).Slice(68))
                : CpuInstance(commands, face).DissolveComplement;
            Assert.Equal(1f, complement);
        }
    }

    static void PrimePointShadowAtlas(MotionTestScene rig)
    {
        // The first requesting frame intentionally renders unshadowed. Allocation happens at the next boundary.
        rig.Scene.Begin();
        rig.Scene.AddLight(new Vector3(0f, 1f, 1f), Color.White, 10f, 1f, LightShadow.Dynamic);
        using RecordingGpuCommandList warmup = Record(rig);
        Assert.Equal(0, rig.Scene.PointShadowedLights);
    }

    static RecordingGpuCommandList Record(MotionTestScene rig)
    {
        rig.Scene.PrepareFrame();
        var commands = new RecordingGpuCommandList(new NullGpuCommandList())
        {
            CapturePayloads = true,
            CaptureBindings = true,
        };
        commands.Begin();
        rig.Scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, rig.Target);
        commands.End();
        return commands;
    }

    static string Fragment(RecordingGpuCommandList.DrawBindings draw)
        => Assert.IsType<FakePipeline>(draw.Pipeline).Request!.Value.FragmentGlsl;

    static bool IsMainFragment(string fragment) => fragment == ShaderSources.ModelFrag
        || fragment == ShaderSources.ModelDissolveFrag || fragment == ShaderSources.SkinnedModelFrag
        || fragment == ShaderSources.SkinnedModelDissolveFrag || fragment == ShaderSources.ModelMotionFrag
        || fragment == ShaderSources.ModelDissolveMotionFrag || fragment == ShaderSources.SkinnedModelMotionFrag
        || fragment == ShaderSources.SkinnedModelDissolveMotionFrag;

    static ReadOnlySpan<byte> UniformBytes(RecordingGpuCommandList commands,
        RecordingGpuCommandList.DrawBindings draw, uint set)
    {
        var (resourceSet, offset) = draw.Sets[set];
        GpuBufferRange range = Assert.Single(Assert.IsType<FakeResourceSet>(resourceSet).Resources.OfType<GpuBufferRange>());
        byte[] bytes = Assert.IsType<byte[]>(Assert.Single(commands.Uploads, u => ReferenceEquals(u.Buffer, range.Buffer)).Data);
        return bytes.AsSpan(checked((int)(range.Offset + offset)), checked((int)range.Size));
    }

    static ModelRenderer.InstanceData CpuInstance(RecordingGpuCommandList commands,
        RecordingGpuCommandList.DrawBindings draw)
    {
        IGpuBuffer buffer = draw.VertexBuffers[1];
        byte[] bytes = Assert.IsType<byte[]>(Assert.Single(commands.Uploads, u => ReferenceEquals(u.Buffer, buffer)).Data);
        return MemoryMarshal.Read<ModelRenderer.InstanceData>(bytes);
    }
}
