using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    /// <summary>
    /// Headless coverage for <see cref="AppRelaunch"/>: the successor is launched with the right executable
    /// and arguments (including the predecessor-wait handshake), the current app is shut down only AFTER a
    /// successful spawn and never when the spawn cannot happen, and the successor-side
    /// <see cref="AppRelaunch.AwaitPredecessor"/> waits on the right pid and strips the handshake from the
    /// arguments. All driven through <see cref="FakeProcessControl"/> so no real process is forked.
    /// </summary>
    public sealed class AppRelaunchTests
    {
        static string Pid(FakeProcessControl pc) => pc.CurrentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        [Fact]
        public void Restart_StartsSuccessor_ThenRequestsShutdown()
        {
            var pc = new FakeProcessControl();
            var req = new RelaunchRequest { RequestShutdown = () => pc.Events.Add("shutdown") };

            RelaunchResult result = AppRelaunch.Restart(req, pc);

            Assert.Equal(RelaunchResult.Started, result);
            Assert.Equal(1, pc.StartCount);
            // The successor must be up before we tear the current app down, or the successor's predecessor-wait
            // would block on a process that has not yet begun exiting.
            Assert.Equal(new[] { "start", "shutdown" }, pc.Events);
        }

        [Fact]
        public void Restart_AppendsPredecessorHandshake_ByDefault()
        {
            var pc = new FakeProcessControl { CurrentProcessId = 4242 };

            AppRelaunch.Restart(new RelaunchRequest(), pc);

            IReadOnlyList<string> args = pc.LastStart!.Arguments;
            Assert.Equal(new[] { AppRelaunch.PredecessorWaitFlag, "4242" }, args.TakeLast(2));
        }

        [Fact]
        public void Restart_CarriesCurrentArgumentsForward()
        {
            var pc = new FakeProcessControl { CurrentCommandLineArguments = new[] { "--profile", "dev" } };

            AppRelaunch.Restart(new RelaunchRequest(), pc);

            Assert.Equal(new[] { "--profile", "dev", AppRelaunch.PredecessorWaitFlag, Pid(pc) }, pc.LastStart!.Arguments);
        }

        [Fact]
        public void Restart_ArgumentsOverride_ReplacesCurrentArguments()
        {
            var pc = new FakeProcessControl { CurrentCommandLineArguments = new[] { "--profile", "dev" } };
            var req = new RelaunchRequest { Arguments = new[] { "--fresh-boot" } };

            AppRelaunch.Restart(req, pc);

            Assert.Equal(new[] { "--fresh-boot", AppRelaunch.PredecessorWaitFlag, Pid(pc) }, pc.LastStart!.Arguments);
        }

        [Fact]
        public void Restart_WaitForPredecessorExitFalse_OmitsHandshake()
        {
            var pc = new FakeProcessControl { CurrentCommandLineArguments = new[] { "--profile", "dev" } };
            var req = new RelaunchRequest { WaitForPredecessorExit = false };

            AppRelaunch.Restart(req, pc);

            Assert.Equal(new[] { "--profile", "dev" }, pc.LastStart!.Arguments);
            Assert.DoesNotContain(AppRelaunch.PredecessorWaitFlag, pc.LastStart!.Arguments);
        }

        [Fact]
        public void Restart_UsesCurrentExecutable_ByDefault()
        {
            var pc = new FakeProcessControl { CurrentExecutablePath = "/apps/MyGame.bin" };

            AppRelaunch.Restart(new RelaunchRequest(), pc);

            Assert.Equal("/apps/MyGame.bin", pc.LastStart!.FileName);
        }

        [Fact]
        public void Restart_ExecutablePathOverride_IsUsed()
        {
            var pc = new FakeProcessControl { CurrentExecutablePath = "/apps/MyGame.bin" };
            var req = new RelaunchRequest { ExecutablePath = "/apps/Relauncher.bin" };

            AppRelaunch.Restart(req, pc);

            Assert.Equal("/apps/Relauncher.bin", pc.LastStart!.FileName);
        }

        [Fact]
        public void Restart_WorkingDirectory_IsForwarded()
        {
            var pc = new FakeProcessControl();
            var req = new RelaunchRequest { WorkingDirectory = "/data/run" };

            AppRelaunch.Restart(req, pc);

            Assert.Equal("/data/run", pc.LastStart!.WorkingDirectory);
        }

        [Fact]
        public void Restart_UnresolvedExecutable_ReturnsUnresolved_AndDoesNotSpawnOrShutDown()
        {
            var pc = new FakeProcessControl { CurrentExecutablePath = null };
            bool shutdownCalled = false;
            var req = new RelaunchRequest { RequestShutdown = () => shutdownCalled = true };

            RelaunchResult result = AppRelaunch.Restart(req, pc);

            Assert.Equal(RelaunchResult.ExecutableUnresolved, result);
            Assert.Equal(0, pc.StartCount);
            Assert.False(shutdownCalled);
        }

        [Fact]
        public void Restart_StartFails_ReturnsStartFailed_AndDoesNotShutDown()
        {
            var pc = new FakeProcessControl { StartThrows = true };
            bool shutdownCalled = false;
            var req = new RelaunchRequest { RequestShutdown = () => shutdownCalled = true };

            RelaunchResult result = AppRelaunch.Restart(req, pc);

            Assert.Equal(RelaunchResult.StartFailed, result);
            Assert.False(shutdownCalled);
        }

        [Fact]
        public void Restart_StaleHandshakeInForwardedArguments_IsReplacedWithFreshOne()
        {
            // A relaunch-of-a-relaunch: the current args already carry a (now-dead) predecessor handshake.
            var pc = new FakeProcessControl
            {
                CurrentProcessId = 5000,
                CurrentCommandLineArguments = new[] { AppRelaunch.PredecessorWaitFlag, "999", "--profile", "dev" },
            };

            AppRelaunch.Restart(new RelaunchRequest(), pc);

            // The stale pid 999 is gone; exactly one fresh handshake carrying the live pid remains.
            Assert.Equal(new[] { "--profile", "dev", AppRelaunch.PredecessorWaitFlag, "5000" }, pc.LastStart!.Arguments);
            Assert.Single(pc.LastStart!.Arguments, a => a == AppRelaunch.PredecessorWaitFlag);
        }

        [Fact]
        public void AwaitPredecessor_NoHandshake_IsNoOp()
        {
            var pc = new FakeProcessControl();
            var args = new[] { "--profile", "dev" };

            PredecessorWait wait = AppRelaunch.AwaitPredecessor(args, process: pc);

            Assert.False(wait.WaitPerformed);
            Assert.True(wait.PredecessorExited);
            Assert.Equal(args, wait.Arguments);
            Assert.Equal(0, pc.WaitCount);
        }

        [Fact]
        public void AwaitPredecessor_WithHandshake_WaitsOnPid_AndStripsToken()
        {
            var pc = new FakeProcessControl { WaitReturns = true };
            var args = new[] { "--profile", "dev", AppRelaunch.PredecessorWaitFlag, "777", "--verbose" };

            PredecessorWait wait = AppRelaunch.AwaitPredecessor(args, process: pc);

            Assert.True(wait.WaitPerformed);
            Assert.True(wait.PredecessorExited);
            Assert.Equal(777, pc.LastWaitPid);
            Assert.Equal(new[] { "--profile", "dev", "--verbose" }, wait.Arguments);
        }

        [Fact]
        public void AwaitPredecessor_Timeout_ReportsPredecessorNotExited()
        {
            var pc = new FakeProcessControl { WaitReturns = false };
            var args = new[] { AppRelaunch.PredecessorWaitFlag, "777" };

            PredecessorWait wait = AppRelaunch.AwaitPredecessor(args, process: pc);

            Assert.True(wait.WaitPerformed);
            Assert.False(wait.PredecessorExited);
        }

        [Fact]
        public void AwaitPredecessor_CustomTimeout_IsPassedThrough()
        {
            var pc = new FakeProcessControl();
            var args = new[] { AppRelaunch.PredecessorWaitFlag, "777" };

            AppRelaunch.AwaitPredecessor(args, TimeSpan.FromSeconds(5), pc);

            Assert.Equal(5000, pc.LastWaitTimeoutMs);
        }

        [Fact]
        public void AwaitPredecessor_DanglingFlagWithNoPid_IsStrippedAndDoesNotWait()
        {
            var pc = new FakeProcessControl();
            var args = new[] { "--profile", AppRelaunch.PredecessorWaitFlag };

            PredecessorWait wait = AppRelaunch.AwaitPredecessor(args, process: pc);

            Assert.False(wait.WaitPerformed);
            Assert.Equal(new[] { "--profile" }, wait.Arguments);
            Assert.Equal(0, pc.WaitCount);
        }

        [Fact]
        public void AwaitPredecessor_FlagFollowedByNonInteger_StripsFlagButKeepsRealArg()
        {
            var pc = new FakeProcessControl();
            var args = new[] { AppRelaunch.PredecessorWaitFlag, "not-a-pid" };

            PredecessorWait wait = AppRelaunch.AwaitPredecessor(args, process: pc);

            Assert.False(wait.WaitPerformed);
            Assert.Equal(new[] { "not-a-pid" }, wait.Arguments);
            Assert.Equal(0, pc.WaitCount);
        }

        [Fact]
        public void Restart_Then_AwaitPredecessor_RoundTrip()
        {
            // The parent builds the successor's arguments; the successor parses them back and waits on the
            // parent's pid, recovering exactly the original launch arguments.
            var parent = new FakeProcessControl
            {
                CurrentProcessId = 1234,
                CurrentCommandLineArguments = new[] { "--level", "3" },
            };
            AppRelaunch.Restart(new RelaunchRequest(), parent);
            IReadOnlyList<string> successorArgs = parent.LastStart!.Arguments;

            var successor = new FakeProcessControl();
            PredecessorWait wait = AppRelaunch.AwaitPredecessor(successorArgs, process: successor);

            Assert.True(wait.WaitPerformed);
            Assert.Equal(1234, successor.LastWaitPid);
            Assert.Equal(new[] { "--level", "3" }, wait.Arguments);
        }

        // --- Resolving the target across both shipped shapes (17.23.0). A self-contained apphost is named by
        // Environment.ProcessPath directly; `dotnet <app>.dll` is not, because ProcessPath is then the SHARED
        // muxer and the dll that says which app to run is dropped along with argv[0]. ---

        [Theory]
        [InlineData("/usr/local/share/dotnet/dotnet")]
        [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]   // the muxer is dotnet.exe on Windows
        public void Restart_DotnetMuxerShape_PutsTheManagedEntryDllBackInFront(string muxer)
        {
            var pc = new FakeProcessControl
            {
                CurrentExecutablePath = muxer,
                CurrentManagedEntryPath = "/apps/Game.dll",
                CurrentCommandLineArguments = new[] { "--profile", "dev" },
            };

            AppRelaunch.Restart(new RelaunchRequest(), pc);

            // Without the dll the successor would be a bare `dotnet` holding the game's arguments.
            Assert.Equal(muxer, pc.LastStart!.FileName);
            Assert.Equal(
                new[] { "/apps/Game.dll", "--profile", "dev", AppRelaunch.PredecessorWaitFlag, Pid(pc) },
                pc.LastStart!.Arguments);
        }

        [Fact]
        public void Restart_ApphostShape_PrependsNothing()
        {
            // The shipped desktop shape. GetCommandLineArgs()[0] is still the dll here, so a rule that keyed off
            // the dll alone rather than off the muxer would corrupt this, the common case.
            var pc = new FakeProcessControl
            {
                CurrentExecutablePath = "/apps/Game",
                CurrentManagedEntryPath = "/apps/Game.dll",
                CurrentCommandLineArguments = new[] { "--profile", "dev" },
            };

            AppRelaunch.Restart(new RelaunchRequest(), pc);

            Assert.Equal("/apps/Game", pc.LastStart!.FileName);
            Assert.Equal(
                new[] { "--profile", "dev", AppRelaunch.PredecessorWaitFlag, Pid(pc) },
                pc.LastStart!.Arguments);
        }

        [Fact]
        public void Restart_ExplicitExecutableOverride_IsNeverRewritten()
        {
            // The caller named its own target, so it owns the whole command line.
            var pc = new FakeProcessControl
            {
                CurrentExecutablePath = "/usr/local/share/dotnet/dotnet",
                CurrentManagedEntryPath = "/apps/Game.dll",
            };
            var req = new RelaunchRequest { ExecutablePath = "/apps/Other" };

            AppRelaunch.Restart(req, pc);

            Assert.Equal("/apps/Other", pc.LastStart!.FileName);
            Assert.DoesNotContain("/apps/Game.dll", pc.LastStart!.Arguments);
        }

        [Fact]
        public void Restart_DotnetMuxerWithNoResolvableEntry_StillStartsRatherThanRefusing()
        {
            // Degrades to the pre-17.23.0 behaviour instead of refusing to relaunch: a successor that may be
            // wrong beats a request that silently does nothing, and Restart never shuts the app down unless the
            // spawn succeeded anyway.
            var pc = new FakeProcessControl
            {
                CurrentExecutablePath = "/usr/local/share/dotnet/dotnet",
                CurrentManagedEntryPath = null,
            };

            Assert.Equal(RelaunchResult.Started, AppRelaunch.Restart(new RelaunchRequest(), pc));
            Assert.Equal(new[] { AppRelaunch.PredecessorWaitFlag, Pid(pc) }, pc.LastStart!.Arguments);
        }
    }
}
