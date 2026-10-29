using System;
using System.Diagnostics;
using System.IO;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed class RendererPerfPowerShellCache : IDisposable
{
    string? _directory;

    internal void Apply(ProcessStartInfo start)
    {
        // Windows LOCALAPPDATA is shared with other tools and must stay unchanged.
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        // pwsh rewrites its multicore JIT startup profile on exit, so parallel fixtures need separate caches.
        // https://github.com/APKiwiOrg/KhaozEngine/issues/1642
        _directory ??= Directory.CreateTempSubdirectory("khaozengine-renderer-perf-pwsh-").FullName;
        start.Environment["XDG_CACHE_HOME"] = _directory;
    }

    public void Dispose()
    {
        if (_directory is not null && Directory.Exists(_directory)) RendererPerfFixtureDirectory.Delete(_directory);
    }
}
