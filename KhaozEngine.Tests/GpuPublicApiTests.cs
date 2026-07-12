using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Reflection guard on the GPU seam's no-leak property: nothing an outside assembly can see on the public
/// surface of <c>KhaozEngine.Gpu</c> may be a Veldrid type. The seam contains Veldrid inside
/// <c>Internal/VeldridGpuDevice</c> and exposes only engine value types / interfaces, so a Veldrid type
/// surfacing in a public or protected signature is an accidental seam breach the compiler would not catch.
/// </summary>
public class GpuPublicApiTests
{
    [Fact]
    public void GpuPublicApi_DoesNotLeakVeldridTypes()
    {
        Assembly gpu = typeof(KhaozEngine.Gpu.GpuDeviceContext).Assembly;
        List<string> leaks = FindLeakedTypes(gpu, "Veldrid");

        bool clean = leaks.Count == 0;
        Assert.True(clean,
            "KhaozEngine.Gpu leaks Veldrid types on its externally visible surface (the GPU seam must keep Veldrid " +
            "contained in Internal/VeldridGpuDevice):\n" + string.Join("\n", leaks));
    }

    // Walks the externally visible (public + protected) surface of every exported type in <paramref name="assembly"/>
    // and returns each place a type declared in an assembly whose simple name starts with
    // <paramref name="forbiddenAssemblyPrefix"/> is reachable. Kept generic (assembly + prefix) so the no-leak
    // property documents any seam, though only Gpu / Veldrid is asserted for now.
    static List<string> FindLeakedTypes(Assembly assembly, string forbiddenAssemblyPrefix)
    {
        var leaks = new List<string>();

        // GetExportedTypes returns exactly the types visible outside the assembly, including nested public types.
        // A genuinely internal type (e.g. VeldridGpuDevice) is absent; a public type in an Internal namespace is
        // present and therefore still checked.
        foreach (Type t in assembly.GetExportedTypes())
        {
            string owner = t.FullName ?? t.Name;
            Collect(leaks, forbiddenAssemblyPrefix, t.BaseType, $"{owner} base type");
            foreach (Type i in t.GetInterfaces())
                Collect(leaks, forbiddenAssemblyPrefix, i, $"{owner} interface");
            if (t.IsGenericTypeDefinition)
                foreach (Type arg in t.GetGenericArguments())
                    foreach (Type constraint in arg.GetGenericParameterConstraints())
                        Collect(leaks, forbiddenAssemblyPrefix, constraint, $"{owner} generic constraint");

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (ConstructorInfo ctor in t.GetConstructors(flags).Where(IsVisible))
                foreach (ParameterInfo p in ctor.GetParameters())
                    Collect(leaks, forbiddenAssemblyPrefix, p.ParameterType, $"{owner}.ctor param {p.Name}");

            foreach (MethodInfo m in t.GetMethods(flags).Where(IsVisible))
            {
                Collect(leaks, forbiddenAssemblyPrefix, m.ReturnType, $"{owner}.{m.Name} return");
                foreach (ParameterInfo p in m.GetParameters())
                    Collect(leaks, forbiddenAssemblyPrefix, p.ParameterType, $"{owner}.{m.Name} param {p.Name}");
                if (m.IsGenericMethodDefinition)
                    foreach (Type arg in m.GetGenericArguments())
                        foreach (Type constraint in arg.GetGenericParameterConstraints())
                            Collect(leaks, forbiddenAssemblyPrefix, constraint, $"{owner}.{m.Name} generic constraint");
            }

            foreach (PropertyInfo pr in t.GetProperties(flags))
                if (pr.GetAccessors(nonPublic: true).Any(IsVisible))
                    Collect(leaks, forbiddenAssemblyPrefix, pr.PropertyType, $"{owner}.{pr.Name} property");

            foreach (FieldInfo f in t.GetFields(flags).Where(IsVisible))
                Collect(leaks, forbiddenAssemblyPrefix, f.FieldType, $"{owner}.{f.Name} field");

            foreach (EventInfo ev in t.GetEvents(flags))
            {
                MethodInfo? add = ev.GetAddMethod(nonPublic: true);
                if (add is not null && IsVisible(add))
                    Collect(leaks, forbiddenAssemblyPrefix, ev.EventHandlerType, $"{owner}.{ev.Name} event");
            }
        }
        return leaks;
    }

    // Records a leak for every concrete leaf type (element types, generic arguments, generic definitions) reached
    // from <paramref name="candidate"/> that lives in a forbidden assembly.
    static void Collect(List<string> leaks, string forbiddenAssemblyPrefix, Type? candidate, string where)
    {
        foreach (Type leaf in Unwrap(candidate))
        {
            string owningAssembly = leaf.Assembly.GetName().Name ?? "";
            if (owningAssembly.StartsWith(forbiddenAssemblyPrefix, StringComparison.Ordinal))
                leaks.Add($"{where} exposes {leaf.FullName} from {owningAssembly}");
        }
    }

    // Externally observable members only: public, or protected (family) so a subclass in another assembly sees it.
    static bool IsVisible(MethodBase m) => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly;

    static bool IsVisible(FieldInfo f) => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly;

    // Expands arrays, by-ref, pointers, and generic arguments down to the concrete leaf types that carry an
    // owning assembly. A generic parameter (a bare T) has no owning library and is skipped.
    static IEnumerable<Type> Unwrap(Type? start)
    {
        if (start is null) yield break;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            Type cur = stack.Pop();
            if (!seen.Add(cur)) continue;

            if (cur.HasElementType)
            {
                Type? element = cur.GetElementType();
                if (element is not null) stack.Push(element);
                continue; // the array / byref / pointer wrapper itself is a BCL type; only the element matters
            }
            if (cur.IsGenericParameter) continue;
            if (cur.IsGenericType)
            {
                foreach (Type arg in cur.GetGenericArguments()) stack.Push(arg);
                yield return cur.GetGenericTypeDefinition(); // check e.g. List<> / the open Veldrid generic itself
                continue;
            }
            yield return cur;
        }
    }
}
