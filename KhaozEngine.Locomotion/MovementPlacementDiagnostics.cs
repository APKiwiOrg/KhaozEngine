using System;

namespace KhaozEngine.Locomotion;

/// <summary>Opt-in thread-local counts of stationary water/boundary validations and identical first-step reuse.</summary>
internal static class MovementPlacementDiagnostics
{
    [ThreadStatic] internal static bool Enabled;
    [ThreadStatic] internal static long Validations;
    [ThreadStatic] internal static long Reuses;

    internal static void Validate()
    {
        if (Enabled) Validations++;
    }

    internal static void Reuse()
    {
        if (Enabled) Reuses++;
    }
}
