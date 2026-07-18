using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordSocketPathsTests
{
    [Fact]
    public void UnixCandidates_UsesXdgRuntimeDirFirst_ThenTmpFallbacks()
    {
        var env = new Dictionary<string, string?>
        {
            ["XDG_RUNTIME_DIR"] = "/run/user/1000",
            ["TMPDIR"] = "/var/tmp-mac",
        };
        List<string> paths = DiscordSocketPaths.UnixCandidates(0, k => env.TryGetValue(k, out var v) ? v : null).ToList();

        Assert.Equal("/run/user/1000/discord-ipc-0", paths[0]);
        Assert.Contains("/var/tmp-mac/discord-ipc-0", paths);
        Assert.Contains("/tmp/discord-ipc-0", paths);
        // sandbox subdirs are derived from each base
        Assert.Contains("/run/user/1000/app/com.discordapp.Discord/discord-ipc-0", paths);
        Assert.Contains("/run/user/1000/snap.discord/discord-ipc-0", paths);
    }

    [Fact]
    public void UnixCandidates_HonorsIndex()
    {
        var env = new Dictionary<string, string?> { ["XDG_RUNTIME_DIR"] = "/run/user/1000" };
        List<string> paths = DiscordSocketPaths.UnixCandidates(3, k => env.TryGetValue(k, out var v) ? v : null).ToList();
        Assert.Equal("/run/user/1000/discord-ipc-3", paths[0]);
    }

    [Fact]
    public void UnixCandidates_SkipsMissingEnvVars_AlwaysIncludesTmp()
    {
        List<string> paths = DiscordSocketPaths.UnixCandidates(0, _ => null).ToList();
        Assert.Contains("/tmp/discord-ipc-0", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("/discord-ipc")); // no empty-base garbage
    }
}
