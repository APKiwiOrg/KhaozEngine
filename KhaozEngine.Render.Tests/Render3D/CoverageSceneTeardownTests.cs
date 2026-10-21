using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageSceneTeardownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scene_teardown_releases_live_history_before_the_final_retirement_flush(bool withSubmission)
    {
        using var h = new CoverageSceneFixture();
        using var submission = new GpuFrameSubmission(h.Capture);
        using var commands = new NullGpuCommandList();
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Scene.PrepareFrame(32, 24);
        submission.Begin();
        h.RecordUncaptured(commands, submission);
        submission.Submit(commands);

        var batch = Assert.IsType<KhaozEngine.Render3D.CoverageGpuBatch>(h.Conformer.Gpu.HistoryBatch);
        var buffers = new[] { batch.StaticData, batch.FrameData, batch.Work, batch.Current, batch.Previous, batch.Results }
            .Select(buffer => Assert.IsType<FakeBuffer>(buffer)).ToArray();
        var bindings = h.Factory.ResourceSets.Where(set => set.Resources.Length == 6
            && ReferenceEquals(set.Resources[0], batch.StaticData)).ToArray();
        Assert.Equal(2, bindings.Length);
        var device = Assert.IsType<FakeGpuDevice>(h.Capture.Inner);
        int drains = device.WaitForIdleCalls;
        device.OnWaitForIdle = () =>
        {
            Assert.All(buffers, buffer => Assert.Equal(0, buffer.DisposeCalls));
            Assert.All(bindings, binding => Assert.Equal(0, binding.DisposeCalls));
        };
        try
        {
            if (withSubmission) h.Scene.DisposeWithSubmission(submission);
            else h.Scene.Dispose();
        }
        finally { device.OnWaitForIdle = null; }

        Assert.True(device.WaitForIdleCalls > drains);
        Assert.Null(h.Conformer.Gpu.HistoryBatch);
        Assert.True(h.Conformer.Disposed);
        Assert.All(buffers, buffer => Assert.Equal(1, buffer.DisposeCalls));
        Assert.All(bindings, binding => Assert.Equal(1, binding.DisposeCalls));
        h.Conformer.Dispose();
        Assert.All(buffers, buffer => Assert.Equal(1, buffer.DisposeCalls));
        Assert.All(bindings, binding => Assert.Equal(1, binding.DisposeCalls));
    }
}
