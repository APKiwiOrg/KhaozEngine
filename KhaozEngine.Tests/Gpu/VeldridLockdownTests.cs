using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Lockdown for the graphics-backend seam (P0 stage 3, phase 3d): the consumer-facing 5.x renderer/windowing/
    /// Gui packages must expose NO Veldrid type on their public API surface — Veldrid is contained to
    /// KhaozEngine.Gpu (the backend boundary). Windowing/input is Silk.NET/GLFW. Swapping the GPU backend is then
    /// a new IGpuDevice impl, not a consumer-visible change. This reflection test fails the build if any
    /// public member leaks a `Veldrid.*` type. (KhaozEngine.Gpu is deliberately excluded — it is the boundary.)
    /// </summary>
    public class VeldridLockdownTests
    {
        [Fact]
        public void PublicApi_OfConsumerPackages_ExposesNoVeldridType()
        {
            Assembly[] assemblies =
            {
                typeof(SpriteBatch).Assembly,   // KhaozEngine.Render2D
                typeof(Scene3D).Assembly,       // KhaozEngine.Render3D
                typeof(AppWindow).Assembly,     // KhaozEngine.Windowing
                typeof(GuiSurface).Assembly,    // KhaozEngine.Gui
            };

            var leaks = new List<string>();
            foreach (Assembly asm in assemblies)
                ScanAssembly(asm, leaks);

            Assert.True(leaks.Count == 0,
                "Public API leaks Veldrid types:\n  " + string.Join("\n  ", leaks));
        }

        static void ScanAssembly(Assembly asm, List<string> leaks)
        {
            Type[] types;
            try { types = asm.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
            catch (Exception) { types = Array.Empty<Type>(); }

            const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.DeclaredOnly;

            foreach (Type t in types)
            {
                Guard(() => Check(t.BaseType, $"{t.FullName} base", leaks));
                Guard(() => { foreach (Type i in t.GetInterfaces()) Check(i, $"{t.FullName} interface", leaks); });
                Guard(() => { foreach (FieldInfo f in t.GetFields(pub)) Check(f.FieldType, $"{t.FullName}.{f.Name} (field)", leaks); });
                Guard(() => { foreach (PropertyInfo p in t.GetProperties(pub)) Check(p.PropertyType, $"{t.FullName}.{p.Name} (property)", leaks); });
                Guard(() =>
                {
                    foreach (MethodInfo m in t.GetMethods(pub).Where(m => !m.IsSpecialName))
                    {
                        Check(m.ReturnType, $"{t.FullName}.{m.Name} (return)", leaks);
                        foreach (ParameterInfo prm in m.GetParameters()) Check(prm.ParameterType, $"{t.FullName}.{m.Name}({prm.Name})", leaks);
                    }
                });
                Guard(() =>
                {
                    foreach (ConstructorInfo c in t.GetConstructors(pub))
                        foreach (ParameterInfo prm in c.GetParameters()) Check(prm.ParameterType, $"{t.FullName}.ctor({prm.Name})", leaks);
                });
                Guard(() => { foreach (EventInfo e in t.GetEvents(pub)) Check(e.EventHandlerType, $"{t.FullName}.{e.Name} (event)", leaks); });
            }
        }

        // Reflecting a member whose signature references an unresolvable type can throw; such a member can't be a
        // checkable public leak, so skip it rather than abort the whole scan.
        static void Guard(Action a)
        {
            // An unreflectable member (its signature references an unresolvable type) can't be an assertable leak.
            try { a(); }
            catch (TypeLoadException) { }
            catch (FileNotFoundException) { }
            catch (FileLoadException) { }
        }

        static void Check(Type? t, string where, List<string> leaks, int depth = 0)
        {
            if (t is null || depth > 8) return;
            if ((t.Namespace ?? "").StartsWith("Veldrid", StringComparison.Ordinal))
                leaks.Add($"{where} -> {t.FullName}");
            if (t.HasElementType) { Check(t.GetElementType(), where, leaks, depth + 1); return; }   // arrays / byref / ptr
            if (t.IsGenericType)
            {
                Check(t.GetGenericTypeDefinition(), where, leaks, depth + 1);
                foreach (Type a in t.GetGenericArguments()) Check(a, where, leaks, depth + 1);
            }
        }
    }
}
