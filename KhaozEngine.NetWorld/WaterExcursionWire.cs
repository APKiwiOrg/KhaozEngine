using System.IO;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

/// <summary>Validates the one-byte portable excursion without serializing local query state.</summary>
internal static class WaterExcursionWire
{
    internal static byte Encode(WaterExcursionState state)
    {
        if (state > WaterExcursionState.AirborneFromWater)
            throw new InvalidDataException("Invalid water excursion state.");
        return (byte)state;
    }

    internal static WaterExcursionState Decode(byte value)
    {
        if (value > (byte)WaterExcursionState.AirborneFromWater)
            throw new InvalidDataException("Invalid water excursion state.");
        return (WaterExcursionState)value;
    }
}
