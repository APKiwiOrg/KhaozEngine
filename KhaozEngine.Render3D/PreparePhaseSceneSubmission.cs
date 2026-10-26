using System;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D;

// Private experiment. Owns only the prefix list. The surface owns the scene and drains it before
// disposing this list, including when the main frame fails after the prefix has already submitted.
internal sealed class PreparePhaseSceneSubmission : IDisposable
{
    readonly IGpuDevice _device;
    readonly Scene3D _scene;
    readonly IGpuCommandList _commands;
    Frame? _preparedFrame;
    long _token;
    bool _disposed;

    internal PreparePhaseSceneSubmission(IGpuDevice device, Scene3D scene)
    {
        if (device.Backend != GpuBackendKind.MetalNative)
            throw new NotSupportedException("The private prepare-phase split requires MetalNative.");
        _device = device;
        _scene = scene;
        _commands = device.Factory.CreateCommandList();
    }

    internal static bool Parse(string? value) => value switch
    {
        null or "" or "0" => false,
        "1" => true,
        _ => throw new ArgumentException("KE_PREPARE_PHASE_SPLIT must be 0 or 1.", nameof(value)),
    };

    internal void Prepare(Frame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_preparedFrame is not null && _scene.IsPreparedOutputCurrent(_token))
            throw new InvalidOperationException("The prepared frame has not been composed.");
        _preparedFrame = null;
        if (frame.RenderSuppressed) { _scene.CancelPreparedOutput(); return; }
        try
        {
            var waitTicket = frame.BeginRecordingWaitObservation(RecordingWaitRole.Prefix, _device, _commands);
            using (GpuRecording.Open(_device, _commands, "the private prepare-phase scene prefix"))
            {
                frame.CompleteRecordingWaitObservation(waitTicket);
                _token = _scene.PrepareOutput(_commands, frame.Width, frame.Height);
            }
            _device.Submit(_commands);
            frame.RecordPreparePhaseSubmission();
            _preparedFrame = frame;
        }
        catch
        {
            _scene.CancelPreparedOutput();
            throw;
        }
    }

    internal void Render(Frame frame, IGpuFramebuffer target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(frame, _preparedFrame) || frame.RenderSuppressed)
            throw new InvalidOperationException("This frame has no submitted scene prefix.");
        _preparedFrame = null;
        try
        {
            _scene.RenderPreparedOutput(frame.Commands, frame.Width, frame.Height, target, _token);
            frame.RecordPreparePhaseComposite();
        }
        catch
        {
            _scene.CancelPreparedOutput();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preparedFrame = null;
        _scene.CancelPreparedOutput();
        _commands.Dispose();
    }
}
