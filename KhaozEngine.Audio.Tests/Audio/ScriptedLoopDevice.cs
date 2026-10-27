using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Audio;

namespace KhaozEngine.Tests.Audio;

internal sealed class ScriptedLoopDevice : ISfxLoopDevice
{
    public List<(string Operation, int Slot)> Calls { get; } = new();
    public List<(int Slot, SfxLoopPlayback Playback)> Starts { get; } = new();
    public List<(int Slot, float Gain)> GainWrites { get; } = new();
    public List<(int Slot, Vector3 Position)> PositionWrites { get; } = new();
    public bool Disposed { get; private set; }
    public Queue<Func<bool>> StartOutcomes { get; } = new();
    public Queue<Func<bool>> StopOutcomes { get; } = new();
    public Queue<Func<bool>> GainOutcomes { get; } = new();
    public Queue<Func<bool>> PositionOutcomes { get; } = new();

    public bool TryStart(int slot, in SfxLoopPlayback playback)
    {
        Calls.Add(("start", slot));
        Starts.Add((slot, playback));
        return Next(StartOutcomes);
    }

    public bool TryStop(int slot)
    {
        Calls.Add(("stop", slot));
        return Next(StopOutcomes);
    }

    public bool TrySetGain(int slot, float gain)
    {
        Calls.Add(("gain", slot));
        GainWrites.Add((slot, gain));
        return Next(GainOutcomes);
    }

    public bool TrySetPosition(int slot, Vector3 position)
    {
        Calls.Add(("position", slot));
        PositionWrites.Add((slot, position));
        return Next(PositionOutcomes);
    }

    public void Dispose()
    {
        Calls.Add(("dispose", -1));
        Disposed = true;
    }

    static bool Next(Queue<Func<bool>> outcomes) => outcomes.Count == 0 || outcomes.Dequeue()();
}
