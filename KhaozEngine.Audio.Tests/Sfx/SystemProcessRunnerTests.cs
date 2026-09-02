using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Sfx;
using Xunit;

namespace KhaozEngine.Tests.Sfx;

/// <summary>
/// Covers the real <see cref="SystemProcessRunner"/> against a child that behaves badly, which is the only part
/// of it a fake cannot stand in for. The children are OS shell one-liners writing into a temp directory the test
/// owns, so nothing here touches a real encoder or any process-global state.
/// </summary>
public sealed class SystemProcessRunnerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ke-sfx-runner-" + Guid.NewGuid().ToString("N"));

    public SystemProcessRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A child that writes `bytes` bytes to stderr and nothing at all to stdout. That is the shape that deadlocks
    // a sequential drain: the reader blocks on stdout until the child exits, and the child cannot exit because it
    // is blocked writing into a stderr pipe buffer nobody is emptying (about 64 KB on the usual platforms).
    (string Exe, string[] Args) FloodStdErr(int bytes)
    {
        string payload = Path.Combine(_dir, "flood.txt");
        File.WriteAllText(payload, new string('x', bytes));
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(_dir, "flood.cmd");
            File.WriteAllText(script, $"@type \"{payload}\" 1>&2\r\n");
            return ("cmd.exe", new[] { "/c", script });
        }
        return ("/bin/sh", new[] { "-c", $"cat \"{payload}\" 1>&2" });
    }

    /// <summary>
    /// A child that floods stderr is drained instead of deadlocking the caller. ffmpeg, the tool this runner
    /// actually launches, emits a lot of stderr on a verbose or unhappy encode, so the classic sequential
    /// ReadToEnd pair (stdout then stderr) hangs ke-sfxbake forever on exactly the inputs a bake operator most
    /// wants a message about.
    /// </summary>
    [Fact]
    public async Task A_child_that_floods_stderr_is_drained_rather_than_deadlocking_the_caller()
    {
        const int Bytes = 512 * 1024;
        (string exe, string[] args) = FloodStdErr(Bytes);
        var runner = new SystemProcessRunner();

        Task<ProcessResult> run = Task.Run(() => runner.Run(exe, args));
        Task finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(finished, run),
            "Run deadlocked on a child that floods stderr, which is the sequential-drain bug");

        ProcessResult result = await run;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StdOut);
        Assert.True(result.StdErr.Length >= Bytes,
            $"stderr was truncated: captured {result.StdErr.Length} of {Bytes} bytes");
    }

    // A child that sits there doing nothing for far longer than the bounded wait under test.
    (string Exe, string[] Args) SleepForever()
    {
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(_dir, "sleep.cmd");
            File.WriteAllText(script, "@ping -n 120 127.0.0.1 > nul\r\n");
            return ("cmd.exe", new[] { "/c", script });
        }
        return ("/bin/sh", new[] { "-c", "sleep 120" });
    }

    /// <summary>
    /// A child that never finishes is killed and reported, instead of parking the bake forever. Without a bound
    /// the only way out of a wedged encoder is for someone to notice and kill ke-sfxbake by hand.
    /// </summary>
    [Fact]
    public async Task A_child_that_never_finishes_is_killed_and_reported_rather_than_waited_on_forever()
    {
        (string exe, string[] args) = SleepForever();
        var runner = new SystemProcessRunner(TimeSpan.FromSeconds(1));

        Task<ProcessResult> run = Task.Run(() => runner.Run(exe, args));
        Task finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(finished, run), "the bounded wait did not bound anything");
        TimeoutException thrown = await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.Contains("killed", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A timeout has to be a real duration. Zero or negative would mean the child is out of time before
    /// it starts, which is a caller bug worth a throw rather than a silent kill on every run.</summary>
    [Fact]
    public void A_non_positive_timeout_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemProcessRunner(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemProcessRunner(TimeSpan.FromSeconds(-1)));
    }
}
