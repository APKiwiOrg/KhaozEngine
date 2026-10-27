using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Audio;
using KhaozEngine.Diagnostics;
using Silk.NET.OpenAL;

namespace KhaozEngine.Tests.Audio;

internal sealed class RecordingSfxAl : ISfxAlCommands
{
    internal sealed class Source
    {
        public uint Buffer;
        public SourceState State = SourceState.Initial;
        public Dictionary<SourceFloat, float> Floats = new();
        public Dictionary<SourceBoolean, bool> Booleans = new();
        public Dictionary<SourceVector3, Vector3> Vectors = new();

        public Source Snapshot() => new()
        {
            Buffer = Buffer,
            State = State,
            Floats = new(Floats),
            Booleans = new(Booleans),
            Vectors = new(Vectors),
        };
    }

    uint _nextName = 1;
    readonly Dictionary<string, Queue<AudioError>> _failures = new();
    readonly HashSet<string> _throws = new();
    public List<(string Operation, uint Object)> Calls { get; } = new();
    public HashSet<uint> LiveSources { get; } = new();
    public HashSet<uint> LiveBuffers { get; } = new();
    public Dictionary<uint, Source> Sources { get; } = new();
    public List<(uint Source, Source State)> Plays { get; } = new();
    public List<(uint Buffer, BufferFormat Format, short[] Samples, int Rate)> Uploads { get; } = new();
    public AudioError PendingError { get; set; }

    public void FailNext(string operation, AudioError error)
    {
        if (!_failures.TryGetValue(operation, out var queue)) _failures.Add(operation, queue = new());
        queue.Enqueue(error);
    }

    public void ThrowNext(string operation) => _throws.Add(operation);

    bool Record(string operation, uint target)
    {
        Calls.Add((operation, target));
        if (_throws.Remove(operation)) throw new InvalidOperationException("Scripted " + operation);
        if (!_failures.TryGetValue(operation, out var queue) || queue.Count == 0) return true;
        Latch(queue.Dequeue());
        return false;
    }

    void Latch(AudioError error)
    {
        if (PendingError == AudioError.NoError) PendingError = error;
    }

    public uint GenSource()
    {
        uint name = _nextName;
        if (!Record("GenSource", name)) return 0;
        _nextName++;
        LiveSources.Add(name);
        Sources.Add(name, new());
        return name;
    }

    public uint GenBuffer()
    {
        uint name = _nextName;
        if (!Record("GenBuffer", name)) return 0;
        _nextName++;
        LiveBuffers.Add(name);
        return name;
    }

    public void BindBuffer(uint source, uint buffer)
    {
        if (!Record("BindBuffer", source)) return;
        if (buffer != 0 && !LiveBuffers.Contains(buffer)) { Latch(AudioError.InvalidName); return; }
        if (Sources[source].State == SourceState.Playing) { Latch(AudioError.InvalidOperation); return; }
        Sources[source].Buffer = buffer;
    }

    public void BufferData(uint buffer, BufferFormat format, ReadOnlySpan<short> samples, int sampleRate)
    {
        if (Record("BufferData", buffer)) Uploads.Add((buffer, format, samples.ToArray(), sampleRate));
    }

    public void SourceStop(uint source)
    {
        if (Record("SourceStop", source)) Sources[source].State = SourceState.Stopped;
    }

    public void SourcePlay(uint source)
    {
        if (!Record("SourcePlay", source)) return;
        if (Sources[source].Buffer == 0) { Latch(AudioError.InvalidOperation); return; }
        Sources[source].State = SourceState.Playing;
        Plays.Add((source, Sources[source].Snapshot()));
    }

    public void DeleteSource(uint source)
    {
        if (Record("DeleteSource", source)) LiveSources.Remove(source);
    }

    public void DeleteBuffer(uint buffer)
    {
        if (!Record("DeleteBuffer", buffer)) return;
        foreach (uint source in LiveSources)
            if (Sources[source].Buffer == buffer) { Latch(AudioError.InvalidOperation); return; }
        LiveBuffers.Remove(buffer);
    }

    public void SetSourceProperty(uint source, SourceFloat property, float value)
    {
        if (Record(property.ToString(), source)) Sources[source].Floats[property] = value;
    }

    public void SetSourceProperty(uint source, SourceBoolean property, bool value)
    {
        if (Record(property.ToString(), source)) Sources[source].Booleans[property] = value;
    }

    public void SetSourceProperty(uint source, SourceVector3 property, Vector3 value)
    {
        if (Record(property.ToString(), source)) Sources[source].Vectors[property] = value;
    }

    public void GetSourceProperty(uint source, GetSourceInteger property, out int value)
    {
        value = 0;
        if (!Record("GetSourceProperty", source)) return;
        value = property == GetSourceInteger.Buffer ? (int)Sources[source].Buffer : (int)Sources[source].State;
    }

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up) => Record("SetListener", 0);

    public AudioError GetError()
    {
        Calls.Add(("GetError", 0));
        AudioError result = PendingError;
        PendingError = AudioError.NoError;
        return result;
    }
}

internal sealed class RecordedSfxFixture : IDisposable
{
    readonly string _path;
    public RecordingSfxAl Commands { get; } = new();
    public InMemorySink Sink { get; } = new();
    public OpenAlSfxBackend Backend { get; }
    public int Buffer { get; }

    public RecordedSfxFixture(ILogger? logger = null)
    {
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Trace };
        options.Sinks.Add(Sink);
        Backend = new OpenAlSfxBackend(Commands, logger ?? new LogManager(options).GetLogger("test"));
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-protected-loops", "task-2");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
        WavSynth.WriteTone(_path, 440, 0.01f, sampleRate: 8000);
        Buffer = Backend.Load(_path);
    }

    public SfxLoopPlayback Playback(bool positional = false) =>
        new(Buffer, 0.7f, 1.2f, positional, new(3, 4, 5), new(2, 0.5f, 90));

    public void Dispose()
    {
        Backend.Dispose();
        File.Delete(_path);
    }
}
