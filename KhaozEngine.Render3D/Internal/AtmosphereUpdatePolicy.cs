namespace KhaozEngine.Render3D.Internal;

/// <summary>Retains the physical values used by static transport. Disabled frames leave that key intact.</summary>
internal sealed class AtmosphereUpdatePolicy
{
    AtmosphereSettings? _physical;

    internal (bool RebuildStatic, bool DrawSkyView) Decide(AtmosphereSettings settings)
    {
        if (!settings.Enabled) return (false, false);
        settings.Validate();
        bool rebuild = !settings.PhysicalValueEquals(_physical);
        if (rebuild) _physical = settings.Clone();
        return (rebuild, true);
    }
}
