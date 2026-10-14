using System;
using System.Collections.Generic;
using KhaozEngine.Windowing;

namespace GestureInputProbe;

internal sealed class ProbeDiagnostics
{
    readonly Queue<string> _recent = new();
    float _logTime;
    long _events;
    string _lastFailure = "none";
    public IEnumerable<string> Recent => _recent;
    public string Summary { get; private set; } = "No pointer samples yet";
    public string Mapping { get; private set; } = "";
    public string Failure { get; private set; } = "none";

    public void Update(AppWindow window, Frame frame, InputManager input)
    {
        _logTime += frame.Dt;
        var state = frame.Input;
        var coordinates = input.Pointer.Coordinates;
        Mapping = $"framebuffer={frame.Width}x{frame.Height} points={frame.LogicalWidth}x{frame.LogicalHeight} "
            + $"inputScale={state.FramebufferScale} viewportScale={coordinates.ViewportScale} "
            + $"pointer={input.Pointer.Position}";
        Failure = window.NativeGestureFailure is { } failure ? $"{failure.Stage}: {failure.Message}" : "none";
        foreach (var sample in state.Gestures.Samples)
        {
            _events++;
            string line = $"{_events} {sample.Kind} {sample.Source} {sample.Phase} "
                + $"delta={sample.ScrollDelta} factor={sample.ScaleFactor:0.####} "
                + $"cmd={sample.CommandDown} momentum={sample.IsMomentum} "
                + $"px={sample.PositionPixels} mapped={coordinates.PositionFromPixels(sample.PositionPixels)}";
            if (_recent.Count == 4) _recent.Dequeue();
            _recent.Enqueue(line);
        }
        Summary = $"streamAvailable={state.Gestures.IsAvailable} frameSamples={state.Gestures.Samples.Count} "
            + $"total={_events} wheel=({state.HorizontalScrollDelta:0.###},{state.ScrollDelta:0.###}) "
            + $"precise={state.HasPreciseScroll} magnify={state.PinchScaleFactor:0.####} "
            + $"focused={state.WindowFocused} captured={state.PointerCaptured}";
        if (Failure != _lastFailure)
        {
            Console.WriteLine($"NativeGestureFailure={Failure}");
            _lastFailure = Failure;
        }
        if (_logTime >= .5f && state.Gestures.Samples.Count > 0)
        {
            Console.WriteLine(Summary);
            _logTime = 0;
        }
    }
}
