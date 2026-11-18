using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Lazy, reusable views over the existing fence scenario. Each keeps its loaded handles across cases.</summary>
public sealed class TemporalDetailSceneFixture : IDisposable
{
    public sealed class View : IDisposable
    {
        internal readonly FenceScene Fence = new(96, 64);
        internal readonly TemporalFixture Frame;

        public View(Func<string, string>? rewrite = null) : this(rewrite, null) { }

        internal View(Func<string, string>? rewrite, IGpuDevice? device)
        {
            Frame = new TemporalFixture(96, 64, s =>
            {
                Fence.Stage.Setup(s, AntiAliasing.Temporal);
                s.Post.Quality.Shadows.Mode = ShadowMode.Off;
            }, device);
            if (rewrite is not null) TemporalTestResolve.Install(Frame, rewrite);
        }

        internal byte[] Draw(float pan = 0f) => Frame.Frame((s, _) => Fence.DrawOverWall(s, pan));
        public void Dispose() => Frame.Dispose();
    }

    readonly Lazy<View> _fine = new(() => new View());
    readonly Lazy<View> _display = new(() => new View());
    readonly Lazy<View> _nearest = new(() => new View(TemporalDetailResultTestSource.Nearest));
    internal View Fine => _fine.Value;
    internal View FineFor(bool nearest) => nearest ? _nearest.Value : Fine;
    internal View Display => _display.Value;

    /// <summary>Align the two views' frame indices, size both to <paramref name="width"/> by
    /// <paramref name="height"/> with a pixel budget that admits exactly that size, draw one frame without the
    /// temporal resolve, and skip both to the start of <paramref name="preset"/>'s jitter cycle, so the next frame
    /// starts a fresh history in the requested modes at phase 0 whatever ran before it in the class.</summary>
    internal void Reset(TemporalUpscale preset, bool hdr = true, bool nearest = false, int width = 96, int height = 64)
    {
        View fine = FineFor(nearest), display = Display;
        long delta = fine.Frame.Scene.CurrentFrameView.FrameIndex - display.Frame.Scene.CurrentFrameView.FrameIndex;
        if (delta > 0) display.Frame.SkipFrames(checked((int)delta));
        if (delta < 0) fine.Frame.SkipFrames(checked((int)-delta));
        // Skip both to where the frame after this reset's own draw opens the preset's jitter cycle, so a row's inputs
        // do not depend on the rows before it and a failure reproduces under a focused filter. Every skip comes before
        // that draw, which latches both views' frame index again, so the next reset aligns on current values.
        int phases = TemporalJitter.PhaseCount(TemporalSettings.DisplayOverInternal(preset));
        long first = Math.Max(fine.Frame.Scene.CurrentFrameView.FrameIndex, display.Frame.Scene.CurrentFrameView.FrameIndex) + 2;
        int skip = (int)((phases - first % phases) % phases);
        fine.Frame.SkipFrames(skip);
        display.Frame.SkipFrames(skip);
        foreach (View view in new[] { fine, display })
        {
            if (view.Frame.DisplayWidth != width || view.Frame.DisplayHeight != height) view.Frame.Resize(width, height);
            Scene3D scene = view.Frame.Scene;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            scene.Post.Hdr.Enabled = hdr;
            scene.Post.Bloom.Enabled = false;
            scene.Post.Temporal.HistoryDetailPixelLimit = (long)width * height;
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
        if (_nearest.IsValueCreated) _nearest.Value.Dispose();
        if (_display.IsValueCreated) _display.Value.Dispose();
    }
}
