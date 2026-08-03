using System;
using System.Linq;
using KhaozEngine.Gpu.D3D11;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ONE ASSERTION DECISION P1 RESTS ON, in one place: nothing this package does off Windows may put the
    /// Direct3D interop into the process. Shared by every test that makes that claim, because the claim is
    /// PROCESS-WIDE, so two copies of it are two copies of the same fact with two chances to drift in what they
    /// say when it breaks.
    /// <para>
    /// A failure here is not a style point. The JIT resolves a method's types when it compiles that method, so an
    /// inlined or unguarded body means a macOS or Linux run loads a Windows-only native binding, and what the
    /// user sees is a startup crash naming an assembly they never asked for. The boundary that prevents it is
    /// every body naming a Vortice type being <c>NoInlining</c> behind
    /// <see cref="KhaozEngineD3D11.IsPlatformSupported"/>.
    /// </para>
    /// <para>
    /// <b>DO NOT ADD A REFLECTION SCAN over the backend assembly to complement this.</b> Reading
    /// <c>ParameterInfo.ParameterType</c> or <c>MethodInfo.ReturnType</c> RESOLVES the type, which loads
    /// <c>Vortice.Direct3D11</c>, <c>Vortice.DirectX</c> and <c>SharpGen.Runtime</c> into the process, and this
    /// assertion is about exactly that list being empty. The scan would pass while making the thing it was
    /// written to protect impossible to observe, and it would take every caller of this helper down with it,
    /// since the load is process-wide and permanent. A check of that shape needs a <c>MetadataLoadContext</c> or
    /// a raw <c>MetadataReader</c>, which resolve nothing into the running process, and neither is worth a
    /// package reference on this row.
    /// </para>
    /// </summary>
    internal static class D3D11InteropLoad
    {
        /// <summary>Fail the calling test if the Direct3D interop is in the process, naming what was loaded.
        /// </summary>
        internal static void AssertNotLoaded()
        {
            string[] loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name ?? "")
                .Where(n => n.StartsWith("Vortice", StringComparison.Ordinal)
                    || n.StartsWith("SharpGen", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Xunit.Assert.True(loaded.Length == 0,
                "The Direct3D interop was loaded on a platform that has none: [" + string.Join(", ", loaded) +
                "]. Either some body that names a Vortice type is no longer NoInlining behind "
                + "KhaozEngineD3D11.IsPlatformSupported, so the JIT resolved those types while compiling a method "
                + "that runs everywhere, or something in this suite resolved a Vortice type through reflection. "
                + "See the note on D3D11InteropLoad for why the second one is not a false alarm to be worked "
                + "around.");
        }
    }
}
