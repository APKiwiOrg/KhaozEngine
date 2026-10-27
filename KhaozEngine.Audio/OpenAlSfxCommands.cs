using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Diagnostics;
using Silk.NET.OpenAL;

namespace KhaozEngine.Audio;

/// <summary>Internal native command boundary. Voice admission remains in the loop and one-shot pools.</summary>
internal interface ISfxAlCommands
{
    uint GenSource();
    uint GenBuffer();
    void BindBuffer(uint source, uint buffer);
    void BufferData(uint buffer, BufferFormat format, ReadOnlySpan<short> samples, int sampleRate);
    void SourceStop(uint source);
    void SourcePlay(uint source);
    void DeleteSource(uint source);
    void DeleteBuffer(uint buffer);
    void SetSourceProperty(uint source, SourceFloat property, float value);
    void SetSourceProperty(uint source, SourceBoolean property, bool value);
    void SetSourceProperty(uint source, SourceVector3 property, Vector3 value);
    void GetSourceProperty(uint source, GetSourceInteger property, out int value);
    void SetListener(Vector3 position, Vector3 forward, Vector3 up);
    AudioError GetError();
}

internal sealed unsafe class SilkSfxAlCommands(AL al) : ISfxAlCommands
{
    public uint GenSource() => al.GenSource();
    public uint GenBuffer() => al.GenBuffer();
    public void BindBuffer(uint source, uint buffer) => al.SetSourceProperty(source, SourceInteger.Buffer, buffer);
    public void BufferData(uint buffer, BufferFormat format, ReadOnlySpan<short> samples, int sampleRate)
    {
        fixed (short* data = samples)
            al.BufferData(buffer, format, data, samples.Length * sizeof(short), sampleRate);
    }

    public void SourceStop(uint source) => al.SourceStop(source);
    public void SourcePlay(uint source) => al.SourcePlay(source);
    public void DeleteSource(uint source) => al.DeleteSource(source);
    public void DeleteBuffer(uint buffer) => al.DeleteBuffer(buffer);
    public void SetSourceProperty(uint source, SourceFloat property, float value) => al.SetSourceProperty(source, property, value);
    public void SetSourceProperty(uint source, SourceBoolean property, bool value) => al.SetSourceProperty(source, property, value);
    public void SetSourceProperty(uint source, SourceVector3 property, Vector3 value) => al.SetSourceProperty(source, property, value);
    public void GetSourceProperty(uint source, GetSourceInteger property, out int value) => al.GetSourceProperty(source, property, out value);
    public void SetListener(Vector3 position, Vector3 forward, Vector3 up)
    {
        al.SetListenerProperty(ListenerVector3.Position, position);
        float* orientation = stackalloc float[6] { forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z };
        al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);
    }

    public AudioError GetError() => al.GetError();
}

/// <summary>Clears stale errors before bounded batches and keeps diagnostics out of cleanup control flow.</summary>
internal sealed class SfxAlCommandBatch(ISfxAlCommands commands, ILogger logger)
{
    readonly AlErrorLog _errors = new(logger);
    readonly HashSet<string> _reportedExceptions = new();

    public void Begin(string operation) => Check(operation + " stale", commands.GetError());

    public bool Complete(string operation) => !Check(operation, commands.GetError());

    public bool TryRun(string operation, Action action)
    {
        try
        {
            Begin(operation);
            action();
            bool succeeded = Complete(operation);
            if (succeeded) _reportedExceptions.Remove(operation);
            return succeeded;
        }
        catch (Exception exception)
        {
            // Operation names are fixed, never source IDs. Success opens a new diagnostic episode.
            if (_reportedExceptions.Add(operation)) ReportException(operation, exception);
            return false;
        }
    }

    public bool Check(string operation, AudioError error)
    {
        // The native outcome is authoritative even when an injected logger throws.
        try { _errors.Check(operation, error); }
        catch (Exception) { }
        return error != AudioError.NoError;
    }

    public void ReportException(string operation, Exception exception)
    {
        try { logger.Error("OpenAL: " + operation + " failed.", exception); }
        catch (Exception) { }
    }
}
