using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Reflection guard on the GPU seam's no-leak property: nothing an outside assembly can see on the public
/// surface of <c>KhaozEngine.Gpu</c> may be a Veldrid type. The Veldrid edge that survives 18.0.0's deletion of
/// the incumbent backend is the SHADER TOOLCHAIN and nothing else, <c>Veldrid.SPIRV</c> plus the base
/// <c>Veldrid</c> assembly its reflection hands description types back from, and the seam keeps that inside
/// method bodies while exposing only engine value types / interfaces. A Veldrid type surfacing in a public or
/// protected signature is an accidental seam breach the compiler would not catch.
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
            "KhaozEngine.Gpu leaks Veldrid types on its externally visible surface (the GPU seam must keep the " +
            "Veldrid.SPIRV shader toolchain inside method bodies):\n" + string.Join("\n", leaks));
    }

    /// <summary>
    /// The same no-leak property for the native Direct3D 11 backend, which falls outside the scan above as
    /// written: it is a different assembly, and its forbidden set is different too.
    /// <para>
    /// <c>Veldrid</c> because the backend's whole premise is being Veldrid-free (decision P2). <c>Vortice</c> and
    /// <c>SharpGen</c> because of decision P1: the package targets <c>net10.0</c> and therefore ships to macOS
    /// and Linux, where its correctness depends on the Direct3D interop never being resolved. A Vortice type in a
    /// public signature would defeat that, since the JIT resolves a member's signature types when it compiles the
    /// member, and a consumer that merely READS such a signature would load the Windows-only assembly on a
    /// platform that has none. The guarded <c>NoInlining</c> bodies are what keep those types inside method
    /// implementations, and this asserts that they stayed there.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Veldrid")]
    [InlineData("Vortice")]
    [InlineData("SharpGen")]
    public void GpuD3D11PublicApi_DoesNotLeakBackendTypes(string forbidden)
    {
        Assembly backend = typeof(KhaozEngine.Gpu.D3D11.KhaozEngineD3D11).Assembly;
        List<string> leaks = FindLeakedTypes(backend, forbidden);

        bool clean = leaks.Count == 0;
        Assert.True(clean,
            $"KhaozEngine.Gpu.D3D11 exposes {forbidden} types on its externally visible surface. Its public " +
            "surface is one class carrying three methods (Register, ValidateShaderPair, ValidateComputeShader) " +
            "and one guard property (IsPlatformSupported), on purpose, and every type from the Direct3D interop " +
            "belongs inside a NoInlining body behind KhaozEngineD3D11.IsPlatformSupported:\n" +
            string.Join("\n", leaks));
    }

    /// <summary>
    /// The same no-leak property for the native Vulkan backend (decision V-P3 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>), whose forbidden set is its own again.
    /// <para>
    /// <c>Veldrid</c> for the reason every native backend carries it: the premise of the phase is Veldrid
    /// leaving the graph. <c>Silk</c> because the binding must stay INSIDE this package. Unlike the Direct3D 11
    /// case the reason is not load-path safety (decision V-P1 needs no platform guard here, and the Vulkan
    /// binding resolves harmlessly on every OS), it is the seam itself: a <c>Silk.NET.Vulkan</c> type on the
    /// public surface would make a consumer that merely reads a signature compile against the Vulkan binding,
    /// which turns an opt-in backend package into a second GPU vocabulary the engine would then owe stability
    /// to. The seam speaks engine types in both directions, and this is what holds it to that.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Veldrid")]
    [InlineData("Silk")]
    public void GpuVulkanPublicApi_DoesNotLeakBackendTypes(string forbidden)
    {
        Assembly backend = typeof(KhaozEngine.Gpu.Vulkan.KhaozEngineVulkan).Assembly;
        List<string> leaks = FindLeakedTypes(backend, forbidden);

        bool clean = leaks.Count == 0;
        Assert.True(clean,
            $"KhaozEngine.Gpu.Vulkan exposes {forbidden} types on its externally visible surface. The Vulkan " +
            "binding is contained inside this package: everything the backend hands the seam is an engine type, " +
            "and everything Silk.NET-shaped belongs behind an internal type:\n" + string.Join("\n", leaks));
    }

    /// <summary>
    /// The same no-leak property for the native Metal backend (decision M-P3 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>), whose forbidden set is ONE entry where its
    /// two siblings have two, and the missing second entry is the interesting part.
    /// <para>
    /// <c>Veldrid</c> for the reason every native backend carries it: the premise of the program is Veldrid
    /// leaving the graph, and vendoring <c>Veldrid.MetalBindings</c> was the rejected alternative to the
    /// hand-rolled interop, so a Veldrid type surfacing here is the exact shape of that decision being reversed.
    /// There is no second row because there is no binding assembly to contain: this package references no
    /// third-party package at all, so the Metal vocabulary it does own is its own internal types rather than a
    /// vendor's, and no assembly-prefix scan can see those. What holds THAT line is
    /// <see cref="GpuMetalPublicSurface_IsExactlyTheApprovedMembers"/>, which asserts the assembly exports one
    /// type, so an interop handle struct cannot become public without moving a list somebody had to read.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Veldrid")]
    public void GpuMetalPublicApi_DoesNotLeakBackendTypes(string forbidden)
    {
        Assembly backend = typeof(KhaozEngine.Gpu.Metal.KhaozEngineMetal).Assembly;
        List<string> leaks = FindLeakedTypes(backend, forbidden);

        bool clean = leaks.Count == 0;
        Assert.True(clean,
            $"KhaozEngine.Gpu.Metal exposes {forbidden} types on its externally visible surface. This package " +
            "is the one whose whole premise is owning the Objective-C interop rather than vendoring a " +
            "Veldrid-derived one, so a Veldrid type reaching this surface is that premise being given up:\n" +
            string.Join("\n", leaks));
    }

    /// <summary>
    /// The IL half of the no-Veldrid-edge guard (decisions P2, V-P3 and M-P3), and the half that actually binds.
    /// <c>ArchitectureTests</c> asserts each backend declares no Veldrid <c>PackageReference</c>, which catches
    /// a deliberate edit to the project file. It cannot catch the subtler failure: a backend reaches Veldrid
    /// through <c>KhaozEngine.Gpu</c>'s transitive closure whatever the project file says, so an INTERNAL helper
    /// signature that mentioned a Veldrid type would compile, would put a Veldrid assembly reference in that
    /// assembly's IL, and would be invisible to every public-surface scan there is. That is precisely why the
    /// cross-compile helper's signatures are engine types, and this is what proves it.
    /// </summary>
    [Theory]
    [InlineData("KhaozEngine.Gpu.D3D11")]
    [InlineData("KhaozEngine.Gpu.Vulkan")]
    [InlineData("KhaozEngine.Gpu.Metal")]
    public void NativeGpuBackend_ReferencesNoVeldridAssembly(string backendAssemblyName)
    {
        Assembly backend = backendAssemblyName switch
        {
            "KhaozEngine.Gpu.D3D11" => typeof(KhaozEngine.Gpu.D3D11.KhaozEngineD3D11).Assembly,
            "KhaozEngine.Gpu.Vulkan" => typeof(KhaozEngine.Gpu.Vulkan.KhaozEngineVulkan).Assembly,
            _ => typeof(KhaozEngine.Gpu.Metal.KhaozEngineMetal).Assembly,
        };

        // Asserted rather than assumed: the ternary above is the one place the theory rows and the assemblies
        // they name could drift apart, and a silent mismatch would run one row twice and the other never.
        Assert.Equal(backendAssemblyName, backend.GetName().Name);

        string[] veldrid = backend.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => n.StartsWith("Veldrid", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        bool clean = veldrid.Length == 0;
        Assert.True(clean,
            backendAssemblyName + " names a type from a Veldrid assembly: [" + string.Join(", ", veldrid) + "]. " +
            "The SPIRV-Cross edge stays behind KhaozEngine.Gpu's internal SpirvCrossCompile helper, whose " +
            "signatures are engine types (CrossCompiledPair / ShaderReflection over GpuVertexElement and " +
            "GpuResourceLayoutDescription) exactly so calling it adds no Veldrid reference here.");
    }

    /// <summary>
    /// The SIZE of the backend's public surface, pinned member by member. The scans above ask what the surface
    /// exposes and say nothing about how much of it there is, so a new public method that happens to leak no
    /// forbidden type is invisible to every one of them, and the message they print (one class, three methods,
    /// one guard property) quietly stops being true. Decision P1 rests on that surface staying small: every
    /// Direct3D type lives inside a <c>NoInlining</c> body behind <see cref="KhaozEngine.Gpu.D3D11.KhaozEngineD3D11.IsPlatformSupported"/>,
    /// and each added member is another place that containment can be got wrong on macOS and Linux.
    /// <para>
    /// So widening the surface is a deliberate edit to the list below, made by someone who had to read this.
    /// Adding a member and updating the array is the whole cost. Not noticing is what this removes.
    /// </para>
    /// </summary>
    [Fact]
    public void GpuD3D11PublicSurface_IsExactlyTheApprovedMembers()
    {
        string[] approvedMembers =
        {
            "IsPlatformSupported", "Register", "ValidateComputeShader", "ValidateShaderPair",
        };

        Type entryPoint = typeof(KhaozEngine.Gpu.D3D11.KhaozEngineD3D11);

        // Property accessors are skipped: get_IsPlatformSupported is the property, already named once.
        string[] members = entryPoint
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m is not MethodBase { IsSpecialName: true })
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(approvedMembers, members);

        // And one exported type, which is the other half of the same claim.
        string[] exported = entryPoint.Assembly.GetExportedTypes()
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "KhaozEngine.Gpu.D3D11.KhaozEngineD3D11" }, exported);
    }

    /// <summary>
    /// The same member-by-member pin for the native Vulkan backend, which the leak scans above cannot give: they
    /// ask what the surface EXPOSES and say nothing about how much of it there is, so a new public method that
    /// happens to name no Silk type is invisible to every one of them.
    /// <para>
    /// The list is one entry, and one entry is the claim. Everything a consumer needs from this package is
    /// <c>Register()</c>: the backend arrives through <c>IGpuBackendProvider</c>, the seam speaks engine types in
    /// both directions, and the Vulkan binding stays inside the package (decision V-P3). There is deliberately no
    /// <c>IsPlatformSupported</c> here, which is the one member the Direct3D 11 sibling has that this must not
    /// grow by analogy: V-P1 says Vulkan is not a Windows API, the loader is resolved at runtime, and a machine
    /// without one is answered by the functional probe rather than by a platform predicate. A guard property
    /// appearing in this list would be that decision quietly reversing.
    /// </para>
    /// <para>
    /// So widening the surface is a deliberate edit to the array below, made by someone who had to read this.
    /// Adding a member and updating it is the whole cost. Not noticing is what this removes.
    /// </para>
    /// </summary>
    [Fact]
    public void GpuVulkanPublicSurface_IsExactlyTheApprovedMembers()
    {
        string[] approvedMembers = { "Register" };

        Type entryPoint = typeof(KhaozEngine.Gpu.Vulkan.KhaozEngineVulkan);

        string[] members = entryPoint
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m is not MethodBase { IsSpecialName: true })
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(approvedMembers, members);

        // And one exported type, which is the other half of the same claim. The provider, the probe, the
        // requirement check and the binding spike are all internal, which is what keeps the package's whole
        // vocabulary out of a consumer's compile.
        string[] exported = entryPoint.Assembly.GetExportedTypes()
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "KhaozEngine.Gpu.Vulkan.KhaozEngineVulkan" }, exported);
    }

    /// <summary>
    /// The same member-by-member pin for the native Metal backend, and here it carries more weight than in
    /// either sibling, because this package has no third-party assembly for a prefix scan to catch. The one
    /// exported type IS the containment: every Objective-C handle struct, every <c>objc_msgSend</c> overload and
    /// the interop spike are internal, so a consumer's compile never sees a Metal vocabulary at all.
    /// <para>
    /// The list is TWO entries, which is one more than either sibling has, and that is decision M-P1 rather
    /// than an accident. <c>Register</c> is the member every backend package has, and
    /// <c>IsPlatformSupported</c> is the Direct3D 11 package's guard arriving for the same reason it exists
    /// there: Metal is an OS-specific API, so the <c>[SupportedOSPlatformGuard]</c> apparatus applies and every
    /// Objective-C body sits behind it. The Vulkan sibling has <c>Register</c> alone and must not grow a guard
    /// by analogy (V-P1), which is why the two lists differ on purpose.
    /// </para>
    /// <para>
    /// So widening the surface is a deliberate edit to the array below, made by someone who had to read this.
    /// Adding a member and updating it is the whole cost. Not noticing is what this removes.
    /// </para>
    /// </summary>
    [Fact]
    public void GpuMetalPublicSurface_IsExactlyTheApprovedMembers()
    {
        string[] approvedMembers = { "IsPlatformSupported", "Register" };

        Type entryPoint = typeof(KhaozEngine.Gpu.Metal.KhaozEngineMetal);

        string[] members = entryPoint
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m is not MethodBase { IsSpecialName: true })
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(approvedMembers, members);

        string[] exported = entryPoint.Assembly.GetExportedTypes()
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "KhaozEngine.Gpu.Metal.KhaozEngineMetal" }, exported);
    }

    // Walks the externally visible (public + protected) surface of every exported type in <paramref name="assembly"/>
    // and returns each place a type declared in an assembly whose simple name starts with
    // <paramref name="forbiddenAssemblyPrefix"/> is reachable. Kept generic (assembly + prefix) so the no-leak
    // property documents any seam, though only Gpu / Veldrid is asserted for now.
    static List<string> FindLeakedTypes(Assembly assembly, string forbiddenAssemblyPrefix)
    {
        var leaks = new List<string>();

        // GetExportedTypes returns exactly the types visible outside the assembly, including nested public types.
        // A genuinely internal type (e.g. SpirvFrontEnd) is absent, and a public type in an Internal namespace
        // is present and therefore still checked.
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
