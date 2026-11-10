using System;
using System.Linq;
using KhaozEngine.Game;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class StandardHostCoverageTests
{
    // #1765. Exercise the production window phases and standard host preparation against an inert device.
    [Theory]
    [InlineData("submit")]
    [InlineData("abandon")]
    [InlineData("fail")]
    public void Host_prepares_before_recording_and_commits_only_successful_submissions(string outcome)
    {
        using var f = new CoveragePublicationFixture();
        var frame = NewFrame(f);
        try
        {
            f.Scene.DrawWater(new WaterPlane(0, 0, 0, 1));
            Run(f, frame, queued: () => Assert.Equal(0, f.Scene.WaterPlaneCount));
            var prior = f.History;
            Assert.NotNull(prior.SurfaceToken);
            f.Evidence.Clear();
            if (outcome == "fail") f.Device.FailSubmission = true;
            if (outcome == "submit") Run(f, frame, 20);
            else Assert.Throws<InvalidOperationException>(() => Run(f, frame, 20, abandon: outcome == "abandon"));
            f.Device.FailSubmission = false;
            Assert.NotEmpty(f.Evidence);
            if (outcome == "submit")
            {
                Assert.Equal(1, f.History.Entry);
                Assert.NotSame(prior.SurfaceToken, f.History.SurfaceToken);
            }
            else Assert.Equal(prior, f.History);
        }
        finally { f.Device.FailSubmission = false; frame.DisposeSubmission(); }
    }

    [Fact]
    public void Full_window_completion_storage_defers_retirement_and_still_commits_covered_history()
    {
        using var f = new CoveragePublicationFixture();
        f.Device.AutoComplete = false;
        var frame = NewFrame(f);
        var outgoing = new System.Collections.Generic.List<FakeResourceSet>();
        var texture = f.Scene.LoadTexture([255, 255, 255, 255], 1, 1);
        try
        {
            for (int i = 0; i < 4; i++)
            {
                int start = f.Device.Resources.ResourceSets.Count;
                var mesh = f.Scene.LoadSkinnedMesh(f.Host.Meshes[0], texture);
                outgoing.AddRange(f.Device.Resources.ResourceSets.Skip(start));
                f.Scene.UnloadSkinnedMesh(mesh);
                Run(f, frame, i == 3 ? 20ul : 10ul);
            }
            Assert.NotEmpty(outgoing);
            Assert.All(outgoing, s => Assert.Equal(0, s.DisposeCount));
            Assert.True(f.Scene.CoverageFailureFor(f.Host.Actor).IsSuccess);
            Assert.Equal(1, f.History.Entry);
            Assert.NotEmpty(f.Evidence);
            // Coverage retention can fail independently of registration. Logical history still commits.
            Assert.NotNull(f.History.SurfaceToken);
            f.Device.WaitForIdle();
            Run(f, frame);
            f.Device.WaitForIdle();
            Run(f, frame);
            Assert.All(outgoing, s => Assert.Equal(1, s.DisposeCount));
            f.Binding.Dispose(); f.Scene.Dispose(); frame.DisposeSubmission();
            Assert.All(outgoing, s => Assert.Equal(1, s.DisposeCount));
        }
        finally { frame.DisposeSubmission(); }
    }

    [Fact]
    public void Zero_viewport_legacy_preparation_and_positive_resize_keep_submitted_history()
    {
        using var f = new CoveragePublicationFixture();
        var frame = NewFrame(f);
        try
        {
            frame.Width = frame.Height = 0;
            Run(f, frame);
            Assert.NotNull(f.History.SurfaceToken);
            frame.Width = 128; frame.Height = 96;
            Run(f, frame);
            Assert.NotNull(f.History.SurfaceToken);
        }
        finally { frame.DisposeSubmission(); }
    }

    static Frame NewFrame(CoveragePublicationFixture f) => new() { Width = 128, Height = 96, Commands = f.Commands };

    static void Run(CoveragePublicationFixture f, Frame frame, ulong key = 10, bool abandon = false, Action? queued = null)
    {
        var before = f.History;
        FramePhases.Run(frame, true, f.Device, f.Commands, Color.Black,
            onPrepare: current =>
            {
                GameApp3D.PrepareScene(f.Scene, _ => { queued?.Invoke(); f.Queue(key); },
                    current.Width, current.Height, current.Submission);
                Assert.Equal(before, f.History);
                Assert.True(f.Scene.CoverageFailureFor(f.Host.Actor).IsSuccess);
                Assert.False(f.Host.Prepare(f.Binding, out _, out var failure));
                Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
            },
            onFrame: current =>
            {
                f.Scene.RenderInternal(current.Commands, current.Width, current.Height, f.Target);
                Assert.Equal(before, f.History);
                if (abandon) throw new InvalidOperationException();
            });
    }
}
