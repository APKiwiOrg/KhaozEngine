using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Platform;

/// <summary>Opens the system default browser at a URL. Best-effort: returns false instead of throwing.
/// The OS seam behind interactive sign-in flows (e.g. KhaozEngine.Identity.Oidc's system-browser launch).</summary>
public static class Browser
{
    public static Task<bool> LaunchBrowserAsync(Uri url, CancellationToken ct = default)
    {
        if (url is null) return Task.FromResult(false);
        try
        {
            OSPlatform os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
                : OSPlatform.Linux;
            Process.Start(CreateStartInfo(url, os));
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    // Windows hands the URL to the shell as the file to open. Elsewhere the URL is one argument in its escaped
    // AbsoluteUri form, so a path with spaces is never split into several arguments.
    internal static ProcessStartInfo CreateStartInfo(Uri url, OSPlatform os)
    {
        if (os == OSPlatform.Windows)
            return new ProcessStartInfo(url.ToString()) { UseShellExecute = true };
        var info = new ProcessStartInfo(os == OSPlatform.OSX ? "open" : "xdg-open");
        info.ArgumentList.Add(url.AbsoluteUri);
        return info;
    }
}
