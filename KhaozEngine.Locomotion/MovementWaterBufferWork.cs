using System;

namespace KhaozEngine.Locomotion;

internal static class MovementWaterBufferWork
{
    [ThreadStatic] internal static bool Enabled;
    [ThreadStatic] internal static int Rents;
    [ThreadStatic] internal static int ClearedReferences;
    internal static void Reset() { Enabled = true; Rents = ClearedReferences = 0; }
}
