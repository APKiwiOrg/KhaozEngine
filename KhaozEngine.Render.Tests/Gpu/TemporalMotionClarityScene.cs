using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Imaging;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One lazy motion stage, with cold history and aligned jitter at every capture boundary.</summary>
public sealed class TemporalMotionClarityScene : IDisposable
{
    const int W = TemporalMotionClarityStage.W, H = TemporalMotionClarityStage.H;
    readonly TemporalDetailGate _gate;
    readonly Dictionary<TemporalDetailGate, TemporalMotionClarityScene> _gates = new();

    public TemporalMotionClarityScene() : this(TemporalDetailGate.Production) { }
    TemporalMotionClarityScene(TemporalDetailGate gate) => _gate = gate;

    internal TemporalMotionClarityScene ForGate(TemporalDetailGate gate)
    {
        if (gate == TemporalDetailGate.Production) return this;
        if (!_gates.TryGetValue(gate, out var source)) _gates.Add(gate, source = new(gate));
        return source;
    }
    readonly Dictionary<(TemporalUpscale, MotionPath, float), Comparison> _comparisons = new();
    readonly TemporalMotionClarityStage _stage = new();
    TemporalFixture? _fixture;
    long _begins;
    (MotionPath, float)? _referenceKey;
    Capture? _reference;
    Measures? _fxaa;
    internal string DeviceName => _fixture!.Device.Capabilities.DeviceName;
    internal string Backend => _fixture!.Device.Backend.ToString();

    internal sealed record Measures(TemporalMotionClarityMeasure.Frame[] Frames,
        TemporalMotionClarityMeasure Detail, TemporalMotionClarityMeasure Texture);
    internal sealed record Capture(byte[][] Images, Measures Measured);
    internal sealed record Comparison(Measures Display, Measures Fine, Measures Fxaa, Measures Reference,
        Measures Settled);

    // Keep measures rather than all capture pixels across xUnit instances of the class.
    internal Comparison Compare(TemporalUpscale preset, MotionPath path, float speed)
    {
        var key = (preset, path, speed);
        if (_comparisons.TryGetValue(key, out Comparison? measured)) return measured;
        if (_referenceKey != (path, speed))
        {
            _reference = Run(TemporalUpscale.Native, path, speed, arm: "R");
            _fxaa = Run(TemporalUpscale.Native, path, speed, arm: "X", reference: _reference.Images).Measured;
            _referenceKey = (path, speed);
        }
        byte[][] reference = _reference!.Images;
        Measures display = Run(preset, path, speed, reference: reference).Measured;
        Measures fine = Run(preset, path, speed, TemporalHistoryDetail.Fine, reference: reference).Measured;
        byte[][] stillReference = Enumerable.Repeat(reference[^1], reference.Length).ToArray();
        Measures settled = Run(preset, path, speed, TemporalHistoryDetail.Fine,
            reference: stillReference, settledStill: true).Measured;
        measured = new Comparison(display, fine, _fxaa!, _reference.Measured, settled);
        _comparisons.Add(key, measured);
        return measured;
    }

    internal Capture Run(TemporalUpscale preset, MotionPath path, float speed,
        TemporalHistoryDetail detail = TemporalHistoryDetail.Display, string arm = "T",
        byte[][]? reference = null, bool settledStill = false, bool collectDecisions = false,
        Action<TemporalFixture, TemporalMotionClarityProbe.Diagnostics>? observe = null)
    {
        int factor = arm == "R" ? 4 : 1;
        bool temporal = arm is not ("R" or "X");
        if (_fixture is null)
        {
            _fixture = new TemporalFixture(W * factor, H * factor);
            if (_gate != TemporalDetailGate.Production)
                TemporalTestResolve.Install(_fixture, source => TemporalDetailGateVariants.Rewrite(source, _gate));
        }
        TemporalFixture fx = _fixture;
        if (fx.DisplayWidth != W * factor) fx.Resize(W * factor, H * factor);
        void Draw(int frame, bool readback, out byte[] pixels)
        {
            void Submit(Scene3D scene, int _) => _stage.Draw(scene, frame, path, speed, settledStill);
            pixels = readback ? fx.Frame(Submit) : Array.Empty<byte>();
            if (!readback) fx.Frames(1, Submit);
            _begins++;
        }

        // An off frame retires the preceding run's history. CameraCut plus phase alignment gives each arm
        // the same cold history and first jitter phase, even after the factor-four reference resized the scene.
        _stage.Setup(fx.Scene, AntiAliasing.Off, TemporalUpscale.Native, factor, TemporalHistoryDetail.Display);
        Draw(0, false, out _);
        _stage.Setup(fx.Scene, arm == "X" ? AntiAliasing.Fxaa : temporal ? AntiAliasing.Temporal : AntiAliasing.Off,
            preset, factor, detail);
        fx.Scene.CameraCut();
        int phases = TemporalJitter.PhaseCount(temporal ? TemporalSettings.DisplayOverInternal(preset) : 1f);
        int pad = (int)((phases - _begins % phases) % phases);
        fx.SkipFrames(pad);
        _begins += pad;
        if (temporal)
            for (int n = 0; n <= TemporalMotionClarityStage.Warm; n++) Draw(n, false, out _);
        else
        {
            fx.SkipFrames(TemporalMotionClarityStage.Warm + 1);
            _begins += TemporalMotionClarityStage.Warm + 1;
        }

        using var probe = collectDecisions && temporal ? new TemporalMotionClarityProbe(fx, _gate) : null;
        int count = TemporalMotionClarityStage.Moving + TemporalMotionClarityStage.Stopped;
        var images = new byte[count][];
        var frames = new TemporalMotionClarityMeasure.Frame[count];
        for (int i = 0; i < count; i++)
        {
            Draw(TemporalMotionClarityStage.Warm + 1 + i, true, out byte[] pixels);
            if (factor > 1) pixels = Rgba8Stats.BoxDownsample(pixels, W * factor, H * factor, factor);
            images[i] = pixels;
            byte[] expected = reference is null ? pixels : reference[i];
            bool pair = i is >= 64 and <= 95;
            byte[]? previous = pair ? images[i - 1] : null;
            byte[]? previousReference = pair ? reference is null ? previous : reference[i - 1] : null;
            var decisions = probe?.Read(fx);
            if (observe is not null && decisions is not null) observe(fx, decisions);
            frames[i] = new(i, i < TemporalMotionClarityStage.Moving ? "moving" : "still",
                TemporalMotionClarityMeasure.Measure(pixels, expected, TemporalMotionClarityMeasure.DetailRegion,
                    previous, previousReference),
                TemporalMotionClarityMeasure.Measure(pixels, expected, TemporalMotionClarityMeasure.TextureRegion,
                    previous, previousReference), decisions);
        }
        return new Capture(images, new Measures(frames, TemporalMotionClarityMeasure.Summarize(frames, true),
            TemporalMotionClarityMeasure.Summarize(frames, false)));
    }

    public void Dispose()
    {
        foreach (var source in _gates.Values) source.Dispose();
        _fixture?.Dispose();
    }
}
