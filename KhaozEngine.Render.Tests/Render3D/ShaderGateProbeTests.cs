using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Device-free half of the private shader gates: what a frame uploads in each state. The executed pixel
/// identity is <see cref="KhaozEngine.Tests.Gpu.ShaderGatePixelIdentityGpuTests"/>.</summary>
public sealed class ShaderGateProbeTests
{
    const int IndirectTail = (int)IndirectUniforms.TailOffset;

    static (Vector4 Indirect, Vector4 Motion, int Applied) Frame(ShaderGateProbe gates)
    {
        using var scene = new MotionTestScene();
        scene.Scene.ForceTemporalForTests = true;
        if (gates != ShaderGateProbe.None) scene.Scene.ProbeShaderGates = gates;
        scene.Scene.PrepareFrame();
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        scene.Scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, scene.Target);
        byte[] motion = commands.ToBuffersOfSize(MotionFrameUbo.SizeInBytes).Last().Data!;
        return (MemoryMarshal.Read<Vector4>(scene.Scene.FrameImageForTests[IndirectTail..]),
            MemoryMarshal.Read<MotionFrameUbo>(motion).Params, scene.Scene.ConsumeAppliedProbeShaderGates());
    }

    [Theory]
    [InlineData(ShaderGateProbe.None, 0f, 0f, 0f)]
    [InlineData(ShaderGateProbe.PointShadowZeroNdl, 1f, 0f, 0f)]
    [InlineData(ShaderGateProbe.UnderwaterMarchZero, 0f, 1f, 0f)]
    [InlineData(ShaderGateProbe.RigidWorldReuse, 0f, 0f, 1f)]
    public void A_frame_uploads_only_its_own_gate_and_reports_what_it_applied(ShaderGateProbe gates, float pointShadow,
        float underwater, float rigid)
    {
        var (indirect, motion, applied) = Frame(gates);
        Assert.Equal(new Vector4(0f, pointShadow, underwater, 0f), indirect);
        Assert.Equal(rigid, motion.Y);
        Assert.Equal((int)gates, applied);
    }

    [Fact]
    public void Off_is_the_default_and_a_frame_with_no_render_reports_none()
    {
        using var scene = new MotionTestScene();
        Assert.Equal(ShaderGateProbe.None, scene.Scene.ProbeShaderGates);
        Assert.Equal(-1, scene.Scene.ConsumeAppliedProbeShaderGates());
        scene.Render();
        Assert.Equal(0, scene.Scene.ConsumeAppliedProbeShaderGates());
        Assert.Equal(-1, scene.Scene.ConsumeAppliedProbeShaderGates());
    }

    [Fact]
    public void Renders_that_disagree_inside_one_callback_are_reported_as_mixed()
    {
        using var scene = new MotionTestScene();
        scene.Render();
        scene.Scene.ProbeShaderGates = ShaderGateProbe.RigidWorldReuse;
        scene.Render();
        Assert.Equal(-2, scene.Scene.ConsumeAppliedProbeShaderGates());
    }

    [Fact]
    public void The_recorder_seam_reaches_the_scene_and_its_bit_names_match_the_switches()
    {
        using var scene = new MotionTestScene();
        var gates = new SceneFrameShaderGates(scene.Scene);
        string[] names = FrameDiagnosticsShaderGateSchedule.GateNames;
        Assert.Equal((int)ShaderGateProbe.PointShadowZeroNdl, 1 << Array.IndexOf(names, "pointShadowZeroNdl"));
        Assert.Equal((int)ShaderGateProbe.UnderwaterMarchZero, 1 << Array.IndexOf(names, "underwaterMarchZero"));
        Assert.Equal((int)ShaderGateProbe.RigidWorldReuse, 1 << Array.IndexOf(names, "rigidWorldReuse"));
        gates.Apply(4);
        Assert.Equal(ShaderGateProbe.RigidWorldReuse, scene.Scene.ProbeShaderGates);
        scene.Render();
        Assert.Equal(4, gates.ConsumeApplied());
    }

    /// <summary>The owner's condition for each gate: exact zero with no epsilon, on the banded value, a bit test that
    /// positive zero alone passes, and reuse confined to slot -1 with the previous camera still applied.</summary>
    [Fact]
    public void Each_gate_tests_exactly_the_condition_that_keeps_the_result_identical()
    {
        Assert.Contains("(IndirectParams.y < 0.5 || ndl > 0.0)", ShaderSources.ModelFrag);
        Assert.Contains("if (IndirectParams.z > 0.5 && floatBitsToUint(visibility) == 0u) break;", ShaderSources.ModelFrag);
        Assert.Contains("PrevViewProj * (MotionParams.y > 0.5 && slot < 0 ? world : prevModel * vec4(Position, 1.0))",
            ShaderSources.ModelMotionVert);
    }
}
