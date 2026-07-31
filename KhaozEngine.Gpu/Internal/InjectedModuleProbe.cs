using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// Enumerates the modules loaded into THIS process and hands their file names to
    /// <see cref="GpuInjectedModules.Match"/>, so device creation can name any third-party overlay that has hooked
    /// the graphics stack. No graphics API is involved, only <see cref="Process"/>, so nothing here needs Vortice
    /// or a live device.
    /// <para>
    /// The whole thing is diagnostics. It must never be able to break device creation, so every failure path
    /// degrades to "not scanned" rather than throwing, and it is a hard no-op off Windows: injectors of this kind
    /// are a Windows phenomenon, and <see cref="Process.Modules"/> is not implemented everywhere.
    /// <see cref="ScanWindows"/> is <see cref="MethodImplOptions.NoInlining"/> so its body is only ever JIT-compiled
    /// on the platform it is allowed to run on, matching the shape of <see cref="D3D11ThreadingProbe"/>.
    /// </para>
    /// </summary>
    internal static class InjectedModuleProbe
    {
        /// <summary>
        /// The known injectors loaded into this process, or null when nothing was scanned. An EMPTY list means the
        /// scan ran and found none, which is a different fact from null and is reported differently.
        /// <paramref name="failure"/> is non-null only when a scan was attempted and did not produce an answer, so
        /// the caller can log the reason. Off Windows there is nothing to attempt, so both come back null and that
        /// is not a fault.
        /// </summary>
        internal static IReadOnlyList<string>? TryScan(out string? failure)
        {
            failure = null;
            // OperatingSystem.IsWindows rather than RuntimeInformation.IsOSPlatform: same answer, and it is the
            // form the platform-compatibility analyzer understands, which is what lets ScanWindows carry
            // [SupportedOSPlatform] without the call site warning.
            if (!OperatingSystem.IsWindows()) return null;

            try
            {
                return ScanWindows();
            }
            catch (Exception ex)
            {
                // Deliberately broad. A diagnostic that takes down device creation is far worse than the problem
                // it was added to diagnose, and reading another process-level structure can fail for reasons that
                // have nothing to do with this engine (a security product blocking the read, a module unloading
                // mid-enumeration).
                failure = $"the loaded-module scan threw {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static IReadOnlyList<string> ScanWindows()
        {
            using Process self = Process.GetCurrentProcess();
            ProcessModuleCollection modules = self.Modules;
            var names = new List<string?>(modules.Count);
            foreach (ProcessModule module in modules) names.Add(module.ModuleName);
            return GpuInjectedModules.Match(names);
        }
    }
}
