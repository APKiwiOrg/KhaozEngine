using System.Collections.Generic;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Physics;

internal static class ConvexQueryFixture
{
    internal static IEnumerable<ConvexQueryShape> Bodies()
    {
        foreach ((float radius, float half) in new[] { (0.2f, 0.25f), (0.9f, 0.95f) })
        {
            float lower = 0.4f, upper = 2f * half, ring = 0.5f * (lower + upper);
            yield return new(ConvexQueryKind.ConeCylinder, radius, lower, ring, upper);
        }
    }
}
