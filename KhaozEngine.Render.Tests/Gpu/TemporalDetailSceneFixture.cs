using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Lazy, reusable views over the existing fence scenario. Each keeps its loaded handles across cases.</summary>
public sealed class TemporalDetailSceneFixture : IDisposable
{
    public sealed class View : IDisposable
    {
        internal readonly FenceScene Fence = new(96, 64);
        internal readonly TemporalFixture Frame;

        public View() => Frame = new TemporalFixture(96, 64, s =>
        {
            Fence.Stage.Setup(s, AntiAliasing.Temporal);
            s.Post.Quality.Shadows.Mode = ShadowMode.Off;
        });

        internal byte[] Draw(float pan = 0f) => Frame.Frame((s, _) => Fence.DrawOverWall(s, pan));
        public void Dispose() => Frame.Dispose();
    }

    readonly Lazy<View> _fine = new(() => new View());
    readonly Lazy<View> _display = new(() => new View());
    internal View Fine => _fine.Value;
    internal View Display => _display.Value;

    internal void Reset(TemporalUpscale preset, bool hdr = true)
    {
        View fine = Fine, display = Display;
        long delta = fine.Frame.Scene.CurrentFrameView.FrameIndex - display.Frame.Scene.CurrentFrameView.FrameIndex;
        if (delta > 0) display.Frame.SkipFrames(checked((int)delta));
        if (delta < 0) fine.Frame.SkipFrames(checked((int)-delta));
        foreach (View view in new[] { fine, display })
        {
            if (view.Frame.DisplayWidth != 96 || view.Frame.DisplayHeight != 64) view.Frame.Resize(96, 64);
            Scene3D scene = view.Frame.Scene;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            scene.Post.Hdr.Enabled = hdr;
            scene.Post.Bloom.Enabled = false;
            scene.Post.Temporal.HistoryDetailPixelLimit = 96L * 64;
            scene.Post.Temporal.Upscale = preset;
            scene.CameraOverride = null;
            view.Draw();
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            scene.Post.Temporal.HistoryDetail = ReferenceEquals(view, fine)
                ? TemporalHistoryDetail.Fine : TemporalHistoryDetail.Display;
        }
    }

    public void Dispose()
    {
        if (_fine.IsValueCreated) _fine.Value.Dispose();
        if (_display.IsValueCreated) _display.Value.Dispose();
    }
}
