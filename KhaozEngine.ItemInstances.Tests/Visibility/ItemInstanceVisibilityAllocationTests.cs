using System;
using System.Reflection;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Visibility;

/// <summary>The allocation measurement runs separately from other collections in this assembly.
/// Functional visibility coverage remains in <see cref="ItemInstanceVisibilityTests"/>.</summary>
[Collection("AllocSensitive")]
public class ItemInstanceVisibilityAllocationTests
{
    [Fact]
    public void PublicView_allocates_nothing_beyond_its_destination_span()
    {
        // The absence of allocation is a property of the SIGNATURE: the view is written into a caller's
        // span and the length comes back as an int, so there is nothing for the method to hand out.
        MethodInfo method = typeof(ItemInstanceVisibility).GetMethod(nameof(ItemInstanceVisibility.PublicView))!;
        Assert.Equal(typeof(int), method.ReturnType);
        Assert.Equal(typeof(Span<byte>), method.GetParameters()[^1].ParameterType);

        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);
        var view = new byte[payload.Length];

        // Warm the path first, so a tier 0 compile is not measured as an allocation.
        for (int index = 0; index < 64; index++)
        {
            ItemInstanceVisibility.PublicView(
                registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, view);
        }

        // A loaded suite can place a one-time tiered JIT allocation in one sample. A real allocation in
        // PublicView appears in every sample, so keep the strict zero bound over the lowest of three runs.
        long leastAllocated = long.MaxValue;
        for (int sample = 0; sample < 3; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 256; index++)
            {
                ItemInstanceVisibility.PublicView(
                    registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, view);
            }
            leastAllocated = Math.Min(leastAllocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, leastAllocated);
    }
}
