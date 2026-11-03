using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Finds direct callers of forbidden exception constructors in the supplied assemblies.</summary>
internal static class RefusalConstructorFence
{
    internal static IReadOnlyList<string> EngineCallers(IEnumerable<Assembly> assemblies,
        IEnumerable<ConstructorInfo> constructors)
    {
        var forbidden = new HashSet<MethodBase>(constructors);
        return assemblies.SelectMany(assembly => assembly.GetTypes())
            .SelectMany(IlCallGraph.DeclaredMethods)
            .Where(method => IlCallGraph.Callees(method).Any(forbidden.Contains))
            .Select(IlCallGraph.Describe)
            .ToArray();
    }
}
