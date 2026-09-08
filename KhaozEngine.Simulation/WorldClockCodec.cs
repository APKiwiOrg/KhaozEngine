using System;
using System.Buffers.Binary;

namespace KhaozEngine.Simulation;

/// <summary>Identifies an authoritative world-clock mutation.</summary>
public enum WorldClockCommandKind : byte
{
    SetTimeOfDay = 0,
    SetTimeScale = 1,
    SetDayLength = 2,
}

/// <summary>A world-clock mutation and its new value.</summary>
public readonly record struct WorldClockCommand(WorldClockCommandKind Kind, float Value);

/// <summary>Encodes and decodes compact world-clock replication payloads.</summary>
public static class WorldClockCodec
{
    private const int StateLength = 12;
    private const int CommandLength = 5;

    /// <summary>Encodes a state as three little-endian single-precision values.</summary>
    public static byte[] EncodeState(WorldClockState state)
    {
        byte[] data = new byte[StateLength];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0, 4), state.TimeOfDay);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4, 4), state.DayLengthSeconds);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8, 4), state.TimeScale);
        return data;
    }

    /// <summary>Decodes a valid, exact-length world-clock state payload.</summary>
    public static bool TryDecodeState(ReadOnlySpan<byte> data, out WorldClockState state)
    {
        state = default;
        if (data.Length != StateLength)
            return false;

        float timeOfDay = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(0, 4));
        float dayLengthSeconds = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(4, 4));
        float timeScale = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(8, 4));
        if (!float.IsFinite(timeOfDay) || timeOfDay < 0f || timeOfDay >= 1f ||
            !float.IsFinite(dayLengthSeconds) || dayLengthSeconds <= 0f ||
            !float.IsFinite(timeScale) || timeScale < 0f)
        {
            return false;
        }

        state = new WorldClockState(timeOfDay, dayLengthSeconds, timeScale);
        return true;
    }

    /// <summary>Encodes a command kind byte followed by one little-endian single-precision value.</summary>
    public static byte[] EncodeCommand(WorldClockCommand command)
    {
        byte[] data = new byte[CommandLength];
        data[0] = (byte)command.Kind;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(1, 4), command.Value);
        return data;
    }

    /// <summary>Decodes a valid, exact-length world-clock command payload.</summary>
    public static bool TryDecodeCommand(ReadOnlySpan<byte> data, out WorldClockCommand command)
    {
        command = default;
        if (data.Length != CommandLength)
            return false;

        WorldClockCommandKind kind = (WorldClockCommandKind)data[0];
        float value = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(1, 4));
        if (!float.IsFinite(value) || !IsValid(kind, value))
            return false;

        command = new WorldClockCommand(kind, value);
        return true;
    }

    private static bool IsValid(WorldClockCommandKind kind, float value) => kind switch
    {
        WorldClockCommandKind.SetTimeOfDay => true,
        WorldClockCommandKind.SetTimeScale => value >= 0f,
        WorldClockCommandKind.SetDayLength => value > 0f,
        _ => false,
    };
}
