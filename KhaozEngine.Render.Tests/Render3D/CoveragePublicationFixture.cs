using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

internal sealed class CoveragePublicationFixture : IDisposable
{
    internal readonly CoveragePublicationDevice Device;
    internal readonly CoverageBindingFixture Host;
    internal readonly GpuFrameSubmission Submission;
    internal readonly HostCoverageBinding Binding;
    internal readonly IGpuFramebuffer Target;
    internal readonly CommandTallyGpuCommandList Commands = new(new NullGpuCommandList());
    internal Scene3D Scene => Host.Scene;
    internal readonly List<CoveragePassEvidence> Evidence = [];

    internal CoveragePublicationFixture(int participants = 8, bool compute = true, bool completion = true, bool precise = true)
    {
        Device = new(compute, completion) { RefusePrecise = !precise };
        Host = new(device: Device);
        Submission = new(Device, consumerCapacity: participants);
        Binding = Host.Binding();
        Assert.True(Binding.TrySetEquipment([Host.Equipment()], out _));
        Scene.Post.Starfield = false;
        Scene.Post.RenderScale = RenderScale.MatchViewport;
        Scene.FrustumCulling = false;
        Target = Device.CreateFramebuffer(null, Device.CreateTexture(GpuTextureDescription.Texture2D(128, 96,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)));
        Scene.CoveragePassObserverForTests = Evidence.Add;
    }
    internal void Begin()
    {
        Submission.Begin(); Scene.BeginFrame(Submission); Commands.Clear(); Evidence.Clear();
    }
    internal SkinnedCoverageFrame Queue(ulong key = 10, bool outlines = false, bool shadowOnly = false,
        HostCoverageBinding? binding = null, MotionKey? actor = null, Matrix4x4? model = null)
    {
        binding ??= Binding;
        bool accepted = binding.TryPrepare(Host.Skeleton, Host.Palette, model ?? Matrix4x4.Identity,
            new(key), CoverageBindingFixture.Template, out var frame, out var failure);
        if (accepted != (key != 30) || !accepted && failure.Code != CoverageFailureCode.TableHole)
            throw new InvalidOperationException("Fixture preparation did not take the requested route.");
        foreach (var equipment in binding.PreparedEquipmentDraws.Span) Scene.Draw(equipment.Rigid);
        var group = outlines ? Scene.BeginMeshOutline(Color.White, 2) : default;
        foreach (var mesh in Host.Hosts)
        {
            var draw = new SkinnedInstanceDraw(mesh, model ?? Matrix4x4.Identity)
            { Motion = actor ?? Host.Actor, Coverage = frame, ShadowOnly = shadowOnly };
            Scene.DrawSkinned(draw, Host.Palette);
            if (outlines) Scene.DrawSkinnedOutline(group, draw, Host.Palette);
        }
        return frame;
    }
    internal void QueueUnsupportedPose()
    {
        var before = Host.Palette[0];
        Host.Palette[0] = Matrix4x4.CreateScale(1 << 21);
        try
        {
            if (Host.Prepare(Binding, out _, out var failure) || failure.Code != CoverageFailureCode.UnsupportedTransform)
                throw new InvalidOperationException("Fixture must take the per-actor transform refusal.");
            foreach (var mesh in Host.Hosts) Scene.DrawSkinned(new(mesh, Matrix4x4.Identity) { Motion = Host.Actor }, Host.Palette);
        }
        finally { Host.Palette[0] = before; }
    }

    internal void Record()
    {
        Scene.PrepareFrame(128, 96);
        Commands.Begin(); Scene.RenderInternal(Commands, 128, 96, Target); Commands.End();
    }
    internal CoverageHistorySnapshot History => Scene.CoverageHistoryForTests(Host.Actor);
    internal void Submit() => Submission.Submit(Commands);
    public void Dispose()
    {
        Submission.Dispose(); Binding.Dispose(); Host.Dispose(); Target.Dispose(); Commands.Dispose(); Device.Dispose();
    }
}
