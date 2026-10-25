using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SceneReplacementRetirementTests
{
    [Theory]
    [InlineData("cascade", false)]
    [InlineData("cascade", true)]
    [InlineData("point", false)]
    [InlineData("point", true)]
    [InlineData("transient", false)]
    [InlineData("transient", true)]
    [InlineData("motion", false)]
    [InlineData("motion", true)]
    [InlineData("hdr", false)]
    [InlineData("hdr", true)]
    [InlineData("msaa", false)]
    [InlineData("msaa", true)]
    [InlineData("resize", false)]
    [InlineData("resize", true)]
    [InlineData("temporal-resize", false)]
    [InlineData("temporal-resize", true)]
    public void Repeated_replacement_keeps_the_outgoing_graph_until_completion(string path, bool shared)
    {
        using var rig = new Rig(path, shared);
        var retired = new List<FakeTexture>();
        for (int change = 1; change <= 2; change++)
        {
            FakeTexture outgoing = rig.Outgoing(path);
            retired.Add(outgoing);
            var before = rig.Snapshot();
            int waits = rig.Device.Waits;
            rig.Replace(path, change);
            Assert.Equal(waits, rig.Device.Waits);
            Assert.NotSame(outgoing, rig.Outgoing(path));
            before.AssertAlive();
            Assert.All(retired, texture => Assert.False(texture.Disposed));
        }
        Assert.True(rig.Scene.RetiredResourceCount > 0);
        rig.Frame();
        Assert.All(retired, texture => Assert.False(texture.Disposed));
        rig.Signal();
        rig.Frame();
        Assert.All(retired, texture => Assert.True(texture.Disposed));
        Assert.False(rig.Outgoing(path).Disposed);
        Assert.Equal(0, rig.Device.Resources.LiveSetsNamingAFreedTexture);
        rig.DisposeScene();
        Assert.Equal(0, rig.Scene.RetiredResourceCount);
        Assert.All(rig.Device.Resources.Textures, texture => Assert.True(texture.Disposed));
        Assert.All(rig.Device.Resources.Pipelines, pipeline => Assert.True(pipeline.Disposed));
    }

    [Theory]
    [InlineData("cascade", false)]
    [InlineData("cascade", true)]
    [InlineData("point", false)]
    [InlineData("point", true)]
    [InlineData("transient", false)]
    [InlineData("transient", true)]
    [InlineData("motion", false)]
    [InlineData("motion", true)]
    [InlineData("hdr", false)]
    [InlineData("hdr", true)]
    [InlineData("msaa", false)]
    [InlineData("msaa", true)]
    [InlineData("resize", false)]
    [InlineData("resize", true)]
    [InlineData("temporal-resize", false)]
    [InlineData("temporal-resize", true)]
    public void Teardown_drains_and_releases_uncompleted_replacements(string path, bool shared)
    {
        using var rig = new Rig(path, shared);
        FakeTexture outgoing = rig.Outgoing(path);
        rig.Replace(path, 1);
        Assert.False(outgoing.Disposed);
        int waits = rig.Device.Waits;
        rig.DisposeScene();
        Assert.True(rig.Device.Waits > waits);
        Assert.True(outgoing.Disposed);
        Assert.Equal(0, rig.Scene.RetiredResourceCount);
        Assert.All(rig.Device.Resources.Textures, texture => Assert.True(texture.Disposed));
        Assert.All(rig.Device.Resources.ResourceSets, set => Assert.True(set.Disposed));
        Assert.All(rig.Device.Resources.Pipelines, pipeline => Assert.True(pipeline.Disposed));
    }

    sealed class Rig : IDisposable
    {
        internal readonly GpuPerfTimestampTestDevice Device = new()
        {
            Features = new(false, true, maxMsaaSampleCount: 4, supportsCompletionFences: true,
                supportsCompute: true)
            { MaxColorAttachments = 8 }
        };
        internal readonly Scene3D Scene;
        readonly IGpuTexture targetTexture;
        readonly IGpuFramebuffer target;
        readonly IGpuCommandList commands;
        readonly GpuFrameSubmission? host;
        readonly List<IGpuFence> pendingFences = new();
        bool disposedScene;
        int viewportWidth = 32, viewportHeight = 16;

        internal Rig(string path, bool shared)
        {
            targetTexture = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                32, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            target = Device.Factory.CreateFramebuffer(null, targetTexture);
            commands = Device.Factory.CreateCommandList();
            if (shared) host = new GpuFrameSubmission(Device);
            Device.OnSubmittedFence = pendingFences.Add;
            Scene = new(Device, target.Outputs, new ShadowSettings { ShadowMapResolution = 256, ShadowCascadeCount = 1 });
            Scene.Post.RenderWidth = 32; Scene.Post.RenderHeight = 16;
            Scene.Post.Hdr.Enabled = false;
            Scene.Post.Quality.AntiAliasing = path == "temporal-resize" ? AntiAliasing.Temporal : AntiAliasing.Off;
            var point = Scene.Post.Quality.Shadows.PointShadows;
            point.FaceResolution = 64; point.MaxShadowedLights = 4; point.MaxDynamicLightsPerFrame = 0;
            if (path is "point" or "transient") Scene.EnsurePointShadowAtlas(64, 4);
            if (path == "transient") Scene.RecordPointShadowTransientDemand(1);
            Frame();
            Signal(); Frame(); Signal(); Frame(); Signal(); Frame();
        }

        internal FakeTexture Outgoing(string path) => (FakeTexture)(path switch
        {
            "cascade" => Device.Resources.Textures.Last(t => !t.Disposed && t.Format == GpuPixelFormat.R32Float
                && t.Height >= 256 && t.Usage.HasFlag(GpuTextureUsage.RenderTarget)),
            "point" => Scene.PointShadowTexture!,
            "transient" => Scene.PointShadowTransientTexture!,
            _ => Scene.ResolvedDepthTarget
        });

        internal Snapshot Snapshot() => new(Device.Resources);

        internal void Replace(string path, int change)
        {
            switch (path)
            {
                case "cascade": Assert.True(Scene.ReplaceShadowLayout(256 * (change + 1), change + 1)); break;
                case "point": Scene.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 4 + change; break;
                case "transient": Scene.RecordPointShadowTransientDemand(change + 1); break;
                case "motion": Scene.Post.Quality.AntiAliasing = change == 1 ? AntiAliasing.Temporal : AntiAliasing.Off; break;
                case "hdr": Scene.Post.Hdr.Enabled = change == 1; break;
                case "msaa": Scene.Post.Quality.AntiAliasing = change == 1 ? AntiAliasing.Msaa(4) : AntiAliasing.Off; break;
                case "temporal-resize": viewportWidth += 8; viewportHeight += 8; break;
                default: Scene.Post.RenderWidth += 8; Scene.Post.RenderHeight += 8; break;
            }
            Frame();
        }

        internal void Frame()
        {
            host?.Begin();
            if (host is null) Scene.Begin(); else Scene.BeginFrame(host);
            Scene.PrepareFrame();
            commands.Begin(); Scene.RenderInternal(commands, viewportWidth, viewportHeight, target); commands.End();
            if (host is null) Device.Submit(commands); else host.Submit(commands);
        }

        internal void Signal()
        {
            foreach (var fence in pendingFences) ((SubmissionTestFence)fence).Signaled = true;
            pendingFences.Clear();
        }
        internal void DisposeScene()
        {
            host?.Dispose();
            Scene.Dispose();
            target.Dispose(); targetTexture.Dispose();
            disposedScene = true;
        }
        public void Dispose()
        {
            if (!disposedScene) DisposeScene();
            commands.Dispose(); Device.Dispose();
        }
    }

    sealed class Snapshot(FakeGpuResourceFactory factory)
    {
        readonly FakeTexture[] textures = factory.Textures.Where(t => !t.Disposed).ToArray();
        readonly FakeFramebuffer[] framebuffers = factory.Framebuffers.Where(f => !f.Disposed).ToArray();
        readonly FakeResourceSet[] sets = factory.ResourceSets.Where(s => !s.Disposed).ToArray();
        readonly FakePipeline[] pipelines = factory.Pipelines.Where(p => !p.Disposed).ToArray();
        readonly FakeShaderSet[] shaders = factory.ShaderSets.Where(s => !s.Disposed).ToArray();
        internal void AssertAlive()
        {
            Assert.All(textures, t => Assert.False(t.Disposed));
            Assert.All(framebuffers, f => Assert.False(f.Disposed));
            Assert.All(sets, s => Assert.False(s.Disposed));
            Assert.All(pipelines, p => Assert.False(p.Disposed));
            Assert.All(shaders, s => Assert.False(s.Disposed));
        }
    }
}
