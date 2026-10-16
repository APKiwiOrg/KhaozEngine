using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageRenderErrorTests
{
    static CoverageRenderStamp Stamp => new(1, 2, 3, MotionKey.From(4), 5);

    [Fact]
    public void Far_absolute_frame_exceeds_render_margin()
    {
        var data = Prepare(Matrix4x4.CreateTranslation(1048576, 0, 0));
        var passes = Passes(data, Vector3.Zero, [Matrix4x4.CreateTranslation(-1048576, 0, 0) * NearVp()]);
        var context = new CoverageRenderContext(data.Topology, Stamp, Vector3.Zero, passes);
        Assert.False(Evaluate(data, context, out var certificate, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.Equal(default, certificate);
    }

    [Fact]
    public void Rebased_near_frame_is_certifiable()
    {
        var origin = new Vector3(1048576);
        var data = Prepare(Matrix4x4.CreateTranslation(origin));
        var passes = Passes(data, origin, ActualVps());
        var context = new CoverageRenderContext(data.Topology, Stamp, origin, passes);
        Assert.True(Evaluate(data, context, out var certificate, out var failure), failure.ToString());
        Assert.True(certificate.FullCoreClearance);
        Assert.Equal(Stamp, certificate.RenderStamp);
        Assert.True(double.IsFinite(certificate.WorldErrorBound));
        Assert.InRange(certificate.WorldErrorBound, double.Epsilon, data.Commands[0].Clearance * CoverageFrames.MarginFraction);
        Assert.True(CoverageCertification.Validate(data.Topology, data.Before, data.Final, data.Actor, data.Commands,
            context, data.Scratch, out var checkedCertificate, out failure), failure.ToString());
        Assert.Equal(certificate, checkedCertificate);
    }

    [Fact]
    public void Actual_pass_vp_does_not_assume_camera_product()
    {
        var data = Prepare(Matrix4x4.Identity);
        var passes = Passes(data, Vector3.Zero, ActualVps());
        var context = new CoverageRenderContext(data.Topology, Stamp, Vector3.Zero, passes);
        Assert.True(Evaluate(data, context, out var certificate, out var failure), failure.ToString());
        // The independently supplied pass becomes ill-conditioned. No camera product can repair it.
        passes[^1] = passes[^1] with { ViewProjection = Matrix4x4.CreateScale(1, 1, 0) };
        Assert.False(Evaluate(data, context, out var refused, out failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.Equal(default, refused);
        Assert.True(certificate.WorldErrorBound > 0);
    }

    [Fact]
    public void Missing_or_mismatched_context_refuses()
    {
        var data = Prepare(Matrix4x4.Identity);
        Assert.False(Evaluate(data, default, out _, out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        var passes = Passes(data, Vector3.Zero, [NearVp()]);
        var foreign = Prepare(Matrix4x4.Identity);
        Check(new(foreign.Topology, Stamp, Vector3.Zero, passes), CoverageFailureCode.IdentityMismatch);
        Check(new(data.Topology, default, Vector3.Zero, passes), CoverageFailureCode.StaleFrame);
        Check(new(data.Topology, Stamp, Vector3.Zero, passes.AsSpan(0, 1)), CoverageFailureCode.IdentityMismatch);
        passes[1] = passes[1] with { EquipmentInstanceId = 99 };
        Check(new(data.Topology, Stamp, Vector3.Zero, passes), CoverageFailureCode.IdentityMismatch);
        passes[1] = passes[1] with { EquipmentInstanceId = 1, Model = Matrix4x4.CreateTranslation(.01f, 0, 0) };
        Check(new(data.Topology, Stamp, Vector3.Zero, passes), CoverageFailureCode.StaleFrame);

        void Check(in CoverageRenderContext context, CoverageFailureCode code)
        {
            Assert.False(Evaluate(data, context, out _, out var refused));
            Assert.Equal(code, refused.Code);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reserved_zero_command_ids_refuse(bool commandId)
    {
        var data = Prepare(Matrix4x4.Identity);
        data.Commands[0] = commandId ? data.Commands[0] with { Id = 0 } : data.Commands[0] with { EquipmentInstanceId = 0 };
        Assert.False(CoverageEvaluator.TryEvaluateGeometry(data.Topology, data.Fixture.Posed, data.Actor, data.Commands,
            data.Scratch, data.Final, out var certificate, out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.Equal(default, certificate);
    }

    [Fact]
    public void Render_refusal_is_atomic()
    {
        var data = Prepare(Matrix4x4.Identity);
        var sentinel = new ModelVertex { Position = new(71, 72, 73), Color = new(2, 3, 4, 5) };
        var passes = Passes(data, Vector3.Zero, [NearVp()]);
        Array.Fill(data.Final, sentinel);
        passes[1] = passes[1] with { PositionPerturbationBound = .01 };
        var context = new CoverageRenderContext(data.Topology, Stamp, Vector3.Zero, passes);
        Assert.False(Evaluate(data, context, out var certificate, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.Equal(default, certificate);
        Assert.All(data.Final, v => Assert.Equal(sentinel, v));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Unknown_shader_position_terms_refuse(double perturbation)
    {
        var data = Prepare(Matrix4x4.Identity);
        var passes = Passes(data, Vector3.Zero, [NearVp()]);
        passes[0] = passes[0] with { PositionPerturbationBound = perturbation };
        var context = new CoverageRenderContext(data.Topology, Stamp, Vector3.Zero, passes);
        Assert.False(Evaluate(data, context, out _, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
    }

    [Fact]
    public void Prepared_bounds_must_contain_the_actual_geometry()
    {
        var data = Prepare(Matrix4x4.Identity);
        var passes = Passes(data, Vector3.Zero, [NearVp()]);
        passes[1] = passes[1] with { LocalMax = new(.5f, .5f, .2f) };
        var context = new CoverageRenderContext(data.Topology, Stamp, Vector3.Zero, passes);
        Assert.False(Evaluate(data, context, out _, out var failure));
        Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Overflow_or_denormal_sensitive_inverse_refuses(bool overflow)
    {
        var data = Prepare(Matrix4x4.Identity);
        var vp = Matrix4x4.Identity;
        vp.M11 = overflow ? float.MaxValue : float.Epsilon;
        var passes = Passes(data, Vector3.Zero, [vp]);
        var context = new CoverageRenderContext(data.Topology, Stamp, Vector3.Zero, passes);
        Assert.False(Evaluate(data, context, out _, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
    }

    [Fact]
    public void Drawable_transition_and_warm_allocation_contracts_hold()
    {
        var data = Prepare(Matrix4x4.Identity);
        data.Commands[0] = data.Commands[0] with { TransitionWeight = 0 };
        var passes = Passes(data, Vector3.Zero, ActualVps());
        // Zero transition returns the original interpolated pose, so capture that exact bound.
        var bounds = Bounds(data.Before.Select(v => v.Position).ToArray());
        for (int i = 0; i < passes.Length; i += 2)
            passes[i] = passes[i] with { LocalMin = bounds.Min, LocalMax = bounds.Max };
        var context = new CoverageRenderContext(data.Topology, Stamp, Vector3.Zero, passes);
        Assert.True(Evaluate(data, context, out var certificate, out var failure), failure.ToString());
        Assert.False(certificate.FullCoreClearance);
        Assert.True(double.IsNaN(certificate.MinimumCoreClearance));
        Assert.Equal(data.Before, data.Final);
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = Evaluate(data, context, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(accepted);
        Assert.Equal(0, allocated);
    }

    static bool Evaluate(Data data, in CoverageRenderContext context, out CoverageCertificate certificate, out CoverageFailure failure) =>
        CoverageEvaluator.TryEvaluate(data.Topology, data.Fixture.Posed, data.Actor, data.Commands, context, data.Scratch,
            data.Final, out certificate, out failure);

    static Data Prepare(Matrix4x4 actor)
    {
        var fixture = CoverageFixture.CurvedPanel(level: 2);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), actor);
        var scratch = new CoverageScratch(topology);
        var final = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluateGeometry(topology, fixture.Posed, actor, commands, scratch, final,
            out var certificate, out var failure), failure.ToString());
        Assert.True(certificate.FullCoreClearance);
        return new(fixture, topology, commands, actor, scratch, CoverageCommands.Surface(topology, fixture.Posed), final);
    }

    static CoverageRenderPass[] Passes(Data data, Vector3 origin, Matrix4x4[] vps)
    {
        var host = Bounds(data.Final.Select(v => v.Position).ToArray());
        var equipment = Bounds(data.Fixture.RigidPositions);
        return vps.SelectMany(vp => new[]
        {
            new CoverageRenderPass(0, 0, Rebase(data.Actor, origin), vp, host.Min, host.Max, 0),
            new CoverageRenderPass(1, 1, Rebase(data.Commands[0].EquipmentWorld, origin), vp, equipment.Min, equipment.Max, 1e-20),
        }).ToArray();
    }

    static Matrix4x4 Rebase(Matrix4x4 matrix, Vector3 origin)
    {
        matrix.M41 -= origin.X; matrix.M42 -= origin.Y; matrix.M43 -= origin.Z;
        return matrix;
    }

    static (Vector3 Min, Vector3 Max) Bounds(Vector3[] positions) =>
        (positions.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min),
            positions.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max));

    static Matrix4x4 NearVp() => Matrix4x4.CreateLookAt(new(0, 0, 4), Vector3.Zero, Vector3.UnitY)
        * Matrix4x4.CreatePerspectiveFieldOfView(1, 1.4f, .1f, 40);

    static Matrix4x4[] ActualVps()
    {
        // Fixed inverted-Y backend convention. These are CPU matrix inputs, not a native rendering proof.
        var caps = new GpuCapabilities(clipSpaceYInverted: true, depthRangeZeroToOne: true);
        var cascade = ShadowMapMath.BuildLightViewProj(new(-.5f, -1, -.4f), Vector3.Zero, radius: 4, resolution: 2048);
        // Scene3D.SetShadowReceiverTail corrects the cascade before applying its X-only atlas column bake.
        var cascadeDepth = GpuClip.Correct(cascade, caps) * ShadowMapMath.AtlasColumnTransform(index: 1, count: 4);
        // PackPointShadowSlotForTarget corrects the finished mirrored face/near-.05/six-column atlas matrix.
        var pointFace = GpuClip.Correct(
            PointShadowMath.FaceViewProjection(face: 5, slot: 1, rows: 4, lightPos: new(0, 0, 4), radius: 25), caps);
        return [GpuClip.Correct(NearVp(), caps), cascadeDepth, pointFace];
    }

    sealed record Data(CoverageFixture Fixture, CoverageRefinement Topology, CoverageCommandPose[] Commands,
        Matrix4x4 Actor, CoverageScratch Scratch, ModelVertex[] Before, ModelVertex[] Final);
}
