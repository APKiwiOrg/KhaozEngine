using System;
using System.Collections.Generic;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Enumerates the candidate unix-domain-socket paths Discord may expose <c>discord-ipc-N</c> on. On
/// macOS/Linux the Discord client puts the socket under a runtime/temp dir; sandboxed installs
/// (Flatpak, Snap) nest it one level deeper. Windows does not use this (it connects to the named pipe
/// <c>discord-ipc-N</c> directly).
/// </summary>
internal static class DiscordSocketPaths
{
    private static readonly string[] EnvBases = { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" };
    private static readonly string[] SandboxSubdirs =
    {
        "app/com.discordapp.Discord",
        "snap.discord",
    };

    public static IEnumerable<string> UnixCandidates(int index, Func<string, string?> getEnv)
    {
        string socket = $"discord-ipc-{index}";
        var seen = new HashSet<string>();
        var bases = new List<string>();

        foreach (string key in EnvBases)
        {
            string? value = getEnv(key);
            if (!string.IsNullOrEmpty(value))
            {
                bases.Add(TrimTrailingSlash(value));
            }
        }

        bases.Add("/tmp");

        foreach (string b in bases)
        {
            foreach (string path in Expand(b, socket))
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> Expand(string baseDir, string socket)
    {
        yield return $"{baseDir}/{socket}";
        foreach (string sub in SandboxSubdirs)
        {
            yield return $"{baseDir}/{sub}/{socket}";
        }
    }

    private static string TrimTrailingSlash(string p) => p.Length > 1 && p.EndsWith('/') ? p[..^1] : p;
}
