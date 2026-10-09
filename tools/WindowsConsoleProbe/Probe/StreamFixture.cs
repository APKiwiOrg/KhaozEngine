using Microsoft.Win32.SafeHandles;

namespace WindowsConsoleValidation;

internal sealed class StreamFixture
{
    // Keep redirected files open through ProcessExit so its newline cannot evade the post-exit check.
    private readonly List<SafeFileHandle> owned = [];
    internal bool HasInput { get; private set; }

    internal void Configure(string mode, string directory, string marker, string outputPipe, string errorPipe)
    {
        // These cases retain the native launch inputs, including cmd's own redirection.
        if (mode.StartsWith("natural-", StringComparison.Ordinal)) return;
        if (mode == "already-console")
        {
            NativeConsole.Require(NativeConsole.AttachConsole(uint.MaxValue), "Fixture AttachConsole");
            return;
        }

        Set(NativeConsole.StdOutput, mode == "invalid" ? new IntPtr(-1) : IntPtr.Zero);
        Set(NativeConsole.StdError, mode == "invalid" ? new IntPtr(-1) : IntPtr.Zero);
        if (mode is "stdout-file" or "fully-file") FileStream(NativeConsole.StdOutput, directory, "stdout.txt");
        if (mode is "stderr-file" or "fully-file") FileStream(NativeConsole.StdError, directory, "stderr.txt");
        if (mode is "stdout-pipe" or "fully-pipe") Set(NativeConsole.StdOutput, new IntPtr(long.Parse(outputPipe)));
        if (mode is "stderr-pipe" or "fully-pipe") Set(NativeConsole.StdError, new IntPtr(long.Parse(errorPipe)));

        if (mode.Contains("file", StringComparison.Ordinal) || mode.Contains("pipe", StringComparison.Ordinal))
        {
            string path = Path.Combine(directory, "stdin.txt");
            File.WriteAllText(path, Evidence.Input(marker));
            SafeFileHandle input = NativeStreams.Open(path, input: true);
            owned.Add(input);
            Set(NativeConsole.StdInput, input.DangerousGetHandle());
            HasInput = true;
        }
    }

    private void FileStream(int which, string directory, string name)
    {
        SafeFileHandle handle = NativeStreams.Open(Path.Combine(directory, name));
        owned.Add(handle);
        Set(which, handle.DangerousGetHandle());
    }

    private static void Set(int which, IntPtr handle) => NativeConsole.Require(NativeConsole.SetStdHandle(which, handle), "Fixture SetStdHandle");
}
