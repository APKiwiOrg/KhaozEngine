using System;
using System.Buffers.Binary;

namespace KhaozEngine.Simulation;

/// <summary>A persisted wall-clock instant and its normalized world time.</summary>
public readonly record struct WorldClockAnchor(double AnchorUnixSeconds, float TimeOfDayAtAnchor)
{
    private const int Magic = 0x4143574B; // "KWCA" in little-endian ASCII.
    private const int CurrentVersion = 1;
    private const int EncodedLength = 20;

    /// <summary>Encodes the anchor as a versioned 20-byte record.</summary>
    public byte[] Encode()
    {
        byte[] data = new byte[EncodedLength];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), CurrentVersion);
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(8, 8), AnchorUnixSeconds);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(16, 4), TimeOfDayAtAnchor);
        return data;
    }

    /// <summary>Decodes a valid anchor record, or returns null for invalid data.</summary>
    public static WorldClockAnchor? Decode(byte[]? data)
    {
        if (data is null || data.Length != EncodedLength)
            return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4)) != Magic)
            return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4)) != CurrentVersion)
            return null;

        double anchorUnixSeconds = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(8, 8));
        float timeOfDayAtAnchor = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(16, 4));
        if (!double.IsFinite(anchorUnixSeconds) ||
            !float.IsFinite(timeOfDayAtAnchor) ||
            timeOfDayAtAnchor < 0f ||
            timeOfDayAtAnchor >= 1f)
            return null;

        return new WorldClockAnchor(anchorUnixSeconds, timeOfDayAtAnchor);
    }

    /// <summary>Advances the anchored time through real elapsed downtime at scale 1.</summary>
    public float RecomputeTimeOfDay(double nowUnixSeconds, float dayLengthSeconds)
    {
        if (!double.IsFinite(nowUnixSeconds) ||
            !double.IsFinite(AnchorUnixSeconds) ||
            !float.IsFinite(TimeOfDayAtAnchor) ||
            !float.IsFinite(dayLengthSeconds) ||
            dayLengthSeconds <= 0f)
        {
            return Wrap(float.IsFinite(TimeOfDayAtAnchor) ? TimeOfDayAtAnchor : 0f);
        }

        double elapsedSeconds = Math.Max(0d, nowUnixSeconds - AnchorUnixSeconds);
        double rawTimeOfDay = TimeOfDayAtAnchor + elapsedSeconds / dayLengthSeconds;
        if (!double.IsFinite(rawTimeOfDay))
            return Wrap(TimeOfDayAtAnchor);

        return Wrap((float)Wrap(rawTimeOfDay));
    }

    private static float Wrap(float value)
    {
        value %= 1f;
        return value < 0f ? value + 1f : value;
    }

    private static double Wrap(double value)
    {
        value %= 1d;
        return value < 0d ? value + 1d : value;
    }
}
