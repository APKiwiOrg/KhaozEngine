using KhaozEngine.Platform;

namespace WindowsConsoleValidation;

internal static class Program
{
    private static StreamFixture? fixture;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 6) return 2;
        string mode = args[0], directory = args[1], marker = args[2], prefix = args[3];
        Evidence.Save(Path.Combine(directory, "started.json"), new { ProcessId = Environment.ProcessId });
        ConsoleState entry = ConsoleState.Capture();
        uint startupFlags = NativeConsole.StartupFlags();
        int parent = NativeConsole.ParentId();
        fixture = new StreamFixture();
        fixture.Configure(mode, directory, marker, args[4], args[5]);
        ConsoleState before = ConsoleState.Capture();
        bool attached = WindowsConsole.EnsureParentConsoleAttached(enable: mode != "opt-out");
        ConsoleState after = ConsoleState.Capture();
        bool hasConsole = WindowsConsole.HasConsole;
        string? failure = null, inputText = null;
        WriteResult? output = null, error = null;
        try
        {
            Console.WriteLine(Evidence.Managed(marker, "STDOUT"));
            Console.Error.WriteLine(Evidence.Managed(marker, "STDERR"));
            Console.Out.Flush();
            Console.Error.Flush();
            output = NativeStreams.Write(NativeConsole.StdOutput, Evidence.Native(marker, "STDOUT"));
            error = NativeStreams.Write(NativeConsole.StdError, Evidence.Native(marker, "STDERR"));
            // Never read console input on a regression failure. That would turn a finite test into a prompt.
            if (fixture.HasInput && StreamState.Capture(NativeConsole.StdInput).FileType == 1)
                inputText = NativeStreams.ReadInput();
        }
        catch (Exception ex) { failure = ex.ToString(); }
        if (mode == "one-shot" && after.HasConsole)
            NativeConsole.Require(NativeConsole.FreeConsole(), "Fixture FreeConsole after successful attach");
        bool second = WindowsConsole.EnsureParentConsoleAttached();
        var value = new ProbeEvidence(marker, mode, Environment.ProcessId, parent, Evidence.EngineVersion,
            startupFlags, entry, before, after, ConsoleState.Capture(), attached, second, hasConsole,
            output, error, inputText, failure);
        Evidence.Save(Path.Combine(directory, "evidence.json"), value);
        using EventWaitHandle ready = EventWaitHandle.OpenExisting(prefix + "-ready");
        using EventWaitHandle release = EventWaitHandle.OpenExisting(prefix + "-release");
        ready.Set();
        return release.WaitOne(TimeSpan.FromSeconds(20)) ? 0 : 124;
    }
}
