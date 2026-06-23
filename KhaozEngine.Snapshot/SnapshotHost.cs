using System;
using System.IO;

namespace KhaozEngine.Snapshot
{
    /// <summary>
    /// One-call CLI host for a game's snapshot tool: resolve the output directory from <c>args[0]</c> (a
    /// deterministic temp default when absent), build a <see cref="SnapshotRunner"/>, run the caller's
    /// registration against it, then print the summary. A game's <c>Program.cs</c> top-level becomes just the
    /// register-the-shots delegate.
    /// </summary>
    public static class SnapshotHost
    {
        /// <summary>Default output directory used when no <c>args[0]</c> is supplied (deterministic, no timestamp).</summary>
        public static string DefaultOutDir => Path.Combine(Path.GetTempPath(), "ke-snapshots");

        /// <summary>
        /// Resolve <c>outDir</c> from <paramref name="args"/>[0] (or <see cref="DefaultOutDir"/>), run
        /// <paramref name="register"/> against a fresh <see cref="SnapshotRunner"/>, emit the summary, and return
        /// the directory the shots were written to.
        /// </summary>
        public static string Run(string[] args, Action<SnapshotRunner> register, Action<string>? log = null)
        {
            if (register is null) throw new ArgumentNullException(nameof(register));
            string outDir = args is { Length: > 0 } && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : DefaultOutDir;
            var runner = new SnapshotRunner(outDir, log);
            register(runner);
            runner.Done();
            return runner.OutDir;
        }

        /// <summary>As <see cref="Run"/>, but returns a process exit code (0) so it can be a <c>Program.cs</c> entry point.</summary>
        public static int Main(string[] args, Action<SnapshotRunner> register, Action<string>? log = null)
        {
            Run(args, register, log);
            return 0;
        }
    }
}
