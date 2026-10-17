using System;

namespace KhaozEngine.Physics;

/// <summary>Asks the next complete footprint query on this thread to publish only the joins support certification
/// reads, those between two polygon members whose upward lower bound is above zero. Every other join bit stays
/// clear, and members, witnesses and verdicts are unchanged. Foot support arms it around its one footprint query and
/// the backend consumes it as that query begins, so every other footprint query, the public raw query included,
/// publishes the full join matrix. Forwarding views pass it through because it travels with the thread.</summary>
internal static class SupportCertificationJoins
{
    [ThreadStatic] static bool t_armed;

    /// <summary>Arms the next footprint query on this thread. Disposing the scope disarms a request no query took.</summary>
    internal static Scope Arm()
    {
        t_armed = true;
        return default;
    }

    /// <summary>Whether the footprint query now beginning was armed. Disarms the thread, so one request serves one
    /// query.</summary>
    internal static bool Take()
    {
        bool armed = t_armed;
        t_armed = false;
        return armed;
    }

    /// <summary>Disarms the thread when the armed call returns or throws.</summary>
    internal readonly struct Scope : IDisposable
    {
        public void Dispose() => t_armed = false;
    }
}
