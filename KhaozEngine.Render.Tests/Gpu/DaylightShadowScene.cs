using System;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One lazy scene for the daylight facts, including spatial references and configuration parity.</summary>
public sealed class DaylightShadowScene : IDisposable
{
    readonly DaylightShadowStage _plain = new(false), _ridges = new(true);
    TemporalFixture? _fixture;
    long _begins;

    internal sealed record Capture(byte[][] Frames, bool[] Refits);

    TemporalFixture Fixture => _fixture ??= new TemporalFixture(DaylightShadowStage.W, DaylightShadowStage.H);
    internal string Backend => Fixture.Device.Backend.ToString();
    internal string DeviceName => Fixture.Device.Capabilities.DeviceName;
    internal object Geometry(bool ridges) => (ridges ? _ridges : _plain).Geometry();

    internal Capture Run(bool ridges, TemporalUpscale preset, float? temporalHold = null,
        bool moving = true, bool reference = false)
    {
        const int W = DaylightShadowStage.W, H = DaylightShadowStage.H;
        int factor = reference ? TemporalAcceptance.SequenceReferenceFactor : 1;
        TemporalFixture fx = Fixture;
        DaylightShadowStage stage = ridges ? _ridges : _plain;
        if (fx.DisplayWidth != W * factor) fx.Resize(W * factor, H * factor);

        // An unheld, non-temporal frame replaces the previous atlas input and retires temporal history.
        // Restore the logical clock in each draw, independently of the shared scene's absolute Begin count.
        stage.Setup(fx.Scene, AntiAliasing.Off, TemporalUpscale.Native, 0f);
        fx.Scene.Post.MaxRenderWidth = W * factor;
        fx.Scene.Post.MaxRenderHeight = H * factor;
        void Draw(int frame, bool readback, out byte[] rgba)
        {
            void Submit(Scene3D s, int _)
            {
                s.EffectTimeSeconds = frame / 60f;
                stage.Draw(s, frame, moving);
            }
            rgba = readback ? fx.Frame(Submit) : Array.Empty<byte>();
            if (!readback) fx.Frames(1, Submit);
            _begins++;
        }
        Draw(0, false, out _);

        stage.Setup(fx.Scene, reference ? AntiAliasing.Off : AntiAliasing.Temporal,
            reference ? TemporalUpscale.Native : preset, reference ? 0f : 1f);
        if (!reference && temporalHold is float hold) fx.Scene.Post.Temporal.ShadowLightHoldTexels = hold;
        fx.Scene.CameraCut();
        int phases = TemporalJitter.PhaseCount(reference ? 1f : TemporalSettings.DisplayOverInternal(preset));
        int pad = (int)((phases - _begins % phases) % phases);
        fx.SkipFrames(pad);
        _begins += pad;
        if (reference)
        {
            fx.SkipFrames(DaylightShadowStage.Warm);
            _begins += DaylightShadowStage.Warm;
        }
        else
            for (int n = 0; n < DaylightShadowStage.Warm; n++) Draw(n, false, out _);

        var frames = new byte[DaylightShadowStage.Count][];
        var refits = new bool[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            Draw(DaylightShadowStage.Warm + i, true, out byte[] rgba);
            frames[i] = reference ? Rgba8Stats.BoxDownsample(rgba, W * factor, H * factor, factor) : rgba;
            refits[i] = fx.Scene.LastShadowPassDiagnostics.LightMatrixChanged;
        }
        return new Capture(frames, refits);
    }

    public void Dispose() => _fixture?.Dispose();
}
