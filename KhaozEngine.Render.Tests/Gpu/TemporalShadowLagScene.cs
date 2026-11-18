using System;
using System.Collections.Generic;
using KhaozEngine.Imaging;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One lazy shadow scene, with cold colour history, light hold and jitter for each independent arm.</summary>
public sealed class TemporalShadowLagScene : IDisposable
{
    readonly Dictionary<string, object> _measurements = new();
    internal bool TryGet<T>(string key, out T? value) where T : class
    {
        value = _measurements.TryGetValue(key, out object? cached) ? (T)cached : null;
        return value is not null;
    }
    internal void Remember(string key, object value) => _measurements[key] = value;
    TemporalDetailGate _gate;
    TemporalFixture? _fixture;
    long _begins;
    internal int Refits { get; private set; }
    internal bool ControlValid { get; private set; }
    internal float ControlJitter { get; private set; }

    internal byte[][] Capture(Action<Scene3D> setup, Action<Scene3D, int> draw, int warm, int count,
        TemporalUpscale preset, TemporalHistoryDetail detail = TemporalHistoryDetail.Display,
        TemporalDetailGate gate = TemporalDetailGate.Production, bool reference = false, bool bilinear = false)
    {
        const int w = 320, h = 180;
        int factor = reference ? TemporalAcceptance.SequenceReferenceFactor : 1;
        _gate = gate;
        if (_fixture is null)
        {
            _fixture = new TemporalFixture(w, h);
            _fixture.Scene.InstallTemporalResolveForTests(source => TemporalDetailGateVariants.Rewrite(source, _gate));
        }
        TemporalFixture fx = _fixture;
        if (fx.DisplayWidth != w * factor) fx.Resize(w * factor, h * factor);
        setup(fx.Scene);
        fx.Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        fx.Scene.Post.Quality.Shadows.ShadowLightHoldTexels = 0f;
        fx.Frames(1, (s, _) => draw(s, 0));
        _begins++;
        // The off frame retires history and fine passes. The retained renderer uses the current capture gate.
        setup(fx.Scene);
        fx.Scene.Post.Temporal.HistoryDetail = detail;
        if (reference)
        {
            fx.Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            fx.Scene.Post.RenderScale = RenderScale.MatchViewport;
            fx.Scene.Post.Supersample = 1f;
            fx.Scene.Post.Pixelated = false;
            fx.Scene.Post.MaxRenderWidth = Math.Max(fx.Scene.Post.MaxRenderWidth, w * factor);
            fx.Scene.Post.MaxRenderHeight = Math.Max(fx.Scene.Post.MaxRenderHeight, h * factor);
            fx.Scene.ForceTemporalForTests = false;
            fx.Scene.DebugView = SceneDebugView.None;
        }
        fx.Scene.CameraCut();
        int phases = TemporalJitter.PhaseCount(reference ? 1f : TemporalSettings.DisplayOverInternal(preset));
        int pad = (int)((phases - _begins % phases) % phases);
        fx.SkipFrames(pad);
        _begins += pad;
        void Draw(Scene3D s, int n)
        {
            draw(s, n);
            if (bilinear) s.TemporalResolveRendererForTests?.VisibilityHistory.ForgetSeedForTests();
        }
        if (reference) { fx.SkipFrames(warm); _begins += warm; }
        else for (int n = 0; n < warm; n++) { int frame = n; fx.Frames(1, (s, _) => Draw(s, frame)); _begins++; }
        Refits = 0;
        var frames = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int frame = warm + i;
            byte[] pixels = fx.Frame((s, _) => Draw(s, frame));
            _begins++;
            frames[i] = reference ? Rgba8Stats.BoxDownsample(pixels, w * factor, h * factor, factor) : pixels;
            if (fx.Scene.LastShadowPassDiagnostics.Rendered) Refits++;
        }
        if (bilinear)
        {
            ControlValid = fx.Scene.TemporalHistory.IsValid;
            ControlJitter = fx.Scene.TemporalResolveRendererForTests!.LastVisibilityUniforms.PreviousJitter.Z;
        }
        return frames;
    }

    public void Dispose() => _fixture?.Dispose();
}
