using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The point caster walk as it stood before <see cref="PointCasterIndex"/>, kept here as the oracle the index is
/// compared against. It visits every caster for every light, which is the cost the index removes, and it is the
/// definition of the right answer: the index must return exactly these slots in exactly this order.
/// </summary>
internal static class PointCasterFullWalk
{
    /// <summary>One caster as the index sees it: its instance slot and its world bounding sphere.</summary>
    internal readonly record struct Caster(int Slot, Vector3 Centre, float Radius);

    /// <summary>Every caster that touches the light's shadowing shell, in the order given, which is ascending slot
    /// order because the scene's run walk adds casters that way.</summary>
    internal static List<int> Slots(IReadOnlyList<Caster> casters, Vector3 light, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax)
    {
        var slots = new List<int>();
        foreach (Caster caster in casters)
            if (Scene3D.InstanceTouchesLight(caster.Centre, caster.Radius, light, radius, nearRadius,
                exclusionMin, exclusionMax))
                slots.Add(caster.Slot);
        return slots;
    }
}
