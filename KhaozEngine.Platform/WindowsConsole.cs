using System;
using System.IO;
using System.Runtime.InteropServices;

namespace KhaozEngine.Platform
{
    /// <summary>
    /// Makes a Windows <c>WinExe</c> (Windows-subsystem) game head viable without losing developer-visible console
    /// output. A Windows-subsystem process opens no console, so a game built as <c>&lt;OutputType&gt;WinExe&lt;/OutputType&gt;</c>
    /// - the way to stop a stray console window opening behind the game - normally has <c>Console.Write*</c> output
    /// vanish when a developer launches it from a terminal (<c>dotnet run</c>, cmd, PowerShell). This attaches the
    /// process to the <b>parent</b> process's console (if there is one) so that stdout/stderr flow back to the
    /// launching terminal, and does nothing at all for a normal Explorer/Start-menu launch (no parent console).
    /// <para>Pure BCL P/Invoke (kernel32), guarded by <see cref="OperatingSystem.IsWindows"/> and wrapped so any
    /// failure degrades to a no-op rather than throwing into startup. A no-op returning <c>false</c> off Windows,
    /// for a console-subsystem process (which already owns a console), when there is no parent console, or when
    /// stdout/stderr are redirected (CI, test runners, piped output are left untouched). Idempotent: it attempts
    /// the attach at most once per process. The macOS/Linux counterpart is a plain <see cref="HasConsole"/> that
    /// always reports a console, because those platforms have no Windows-subsystem/no-console problem.</para>
    /// </summary>
    public static class WindowsConsole
    {
        // AttachConsole(ATTACH_PARENT_PROCESS) attaches to the console of the parent process. (DWORD)-1.
        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        private static readonly object gate = new();
        private static bool attempted;

        /// <summary>
        /// Whether the process currently owns a console window (Win32 <c>GetConsoleWindow() != 0</c>). True after a
        /// successful <see cref="EnsureParentConsoleAttached"/>, and true for a console-subsystem exe. Off Windows
        /// this always returns <c>true</c>: the no-console case this type guards is Windows-subsystem-specific, and
        /// a POSIX process always has a controlling stdout. Never throws (an unknown state reports <c>true</c> so a
        /// caller's no-console fallback does not fire spuriously).
        /// </summary>
        public static bool HasConsole
        {
            get
            {
                if (!OperatingSystem.IsWindows()) return true;
                try { return GetConsoleWindow() != IntPtr.Zero; }
                catch { return true; }
            }
        }

        /// <summary>
        /// Attach the process to its parent's console (once) and rewire <see cref="Console.Out"/>/<see cref="Console.Error"/>
        /// to it, so a <c>WinExe</c> head's stdout/stderr reach the launching terminal. Returns <c>true</c> only when
        /// THIS call attached and rewired a console. A no-op returning <c>false</c> when: already attempted; disabled
        /// via <paramref name="enable"/>; off Windows; a console already exists (console-subsystem exe or a prior
        /// attach); there is no parent console (Explorer/Start launch); or both stdout and stderr are already
        /// redirected (a pipe/file/CI capture is respected and left untouched - only the non-redirected streams are
        /// rewired). Idempotent and never throws, so it is safe as the very first line of startup. Passing
        /// <paramref name="enable"/> <c>false</c> is the opt-out: it still marks the one-shot as spent so a later
        /// belt-and-suspenders call cannot re-enable it.
        /// </summary>
        public static bool EnsureParentConsoleAttached(bool enable = true)
        {
            lock (gate)
            {
                if (attempted) return false;
                attempted = true;
            }

            if (!enable) return false;
            if (!OperatingSystem.IsWindows()) return false;

            bool outputRedirected, errorRedirected, hasConsole;
            try
            {
                // Read redirection BEFORE attaching: a redirected handle (a pipe or `> out.txt`, and CI/test-runner
                // capture) must be left pointing at its target. AttachConsole itself never overwrites an already-set
                // standard handle, but we also skip rewiring those streams below.
                outputRedirected = Console.IsOutputRedirected;
                errorRedirected = Console.IsErrorRedirected;
                hasConsole = GetConsoleWindow() != IntPtr.Zero;
            }
            catch
            {
                return false;
            }

            if (!ShouldAttach(isWindows: true, enable: true, hasConsole, outputRedirected, errorRedirected))
                return false;

            try
            {
                if (!AttachConsole(ATTACH_PARENT_PROCESS)) return false; // no parent console (normal GUI launch)
                RewireStreams(outputRedirected, errorRedirected);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Pure attach decision, factored out so it is unit-testable on any OS. Attach only on Windows, when
        /// enabled, when no console exists yet, and when at least one of stdout/stderr is not already redirected
        /// (so a fully-redirected process - both streams captured - is left completely alone).
        /// </summary>
        internal static bool ShouldAttach(bool isWindows, bool enable, bool hasConsole, bool outputRedirected, bool errorRedirected)
            => isWindows && enable && !hasConsole && (!outputRedirected || !errorRedirected);

        private static void RewireStreams(bool outputRedirected, bool errorRedirected)
        {
            // Point Console.Out/Error at the freshly-attached console. Only the streams that were NOT redirected are
            // rewired; a redirected one keeps flowing to its pipe/file. AutoFlush so a GUI process that never runs a
            // clean shutdown still gets its output out (and a crash keeps what was written).
            bool rewired = false;
            if (!outputRedirected)
            {
                var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(writer);
                rewired = true;
            }
            if (!errorRedirected)
            {
                var writer = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(writer);
                rewired = true;
            }
            if (!rewired) return;

            // Trailing-newline courtesy. A Windows GUI-subsystem process detaches from the shell immediately, so the
            // shell prints its next prompt before the app's output arrives; the two interleave. Emitting one newline
            // at process exit keeps the shell's prompt on its own line instead of jammed against the app's last
            // output line - the norm for an attached GUI app, no worse. Best effort; never throws.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    Console.Out.Flush();
                    Console.Out.Write(Environment.NewLine);
                    Console.Out.Flush();
                }
                catch
                {
                    // process is tearing down; nothing useful to do.
                }
            };
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
    }
}
