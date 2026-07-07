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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url.ToString());
            else
                Process.Start("xdg-open", url.ToString());
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
