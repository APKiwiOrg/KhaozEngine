namespace WindowsConsoleValidation;

internal sealed class ConsoleChecks(string probeExe, string root)
{
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal string Run(ConsoleSession console, CaseSpec spec)
    {
        using var scenario = new Scenario(root, spec);
        string executable = probeExe, arguments = scenario.Arguments(spec.Mode);
        if (spec.Batch)
        {
            // A live BATCH cmd waits for a WinExe. An interactive cmd /c may exit before AttachConsole.
            string batch = Path.Combine(scenario.DirectoryPath, "launch.cmd");
            string redirection = spec.Mode switch
            {
                "natural-stdout-file" => " >" + NativeProcess.Quote(Path.Combine(scenario.DirectoryPath, "stdout.txt")),
                "natural-stderr-file" => " 2>" + NativeProcess.Quote(Path.Combine(scenario.DirectoryPath, "stderr.txt")),
                _ => string.Empty
            };
            File.WriteAllText(batch, "@echo off\r\n" + NativeProcess.Quote(probeExe) + " " + arguments
                + redirection + "\r\nexit /b %errorlevel%\r\n");
            executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            arguments = "/d /s /c \"" + NativeProcess.Quote(batch) + "\"";
        }
        using NativeProcess parentOrProbe = NativeProcess.Start(executable, arguments,
            inheritConsole: spec.Batch || spec.StartupHandles);
        ProbeEvidence value = scenario.Read();
        bool parentAlive = !parentOrProbe.Process.HasExited;
        uint[] liveIds = NativeConsole.ProcessIds();
        string buffer = console.Read();
        Evidence.Save(Path.Combine(scenario.DirectoryPath, "driver-evidence.json"), new
        {
            DriverProcessId = Environment.ProcessId, LaunchProcessId = parentOrProbe.Process.Id,
            ParentAlive = parentAlive, ConsoleWindow = console.Window, LiveProcessIds = liveIds,
            Launch = spec.Batch ? "cmd BATCH" : "native CreateProcessW", spec.StartupHandles
        });
        File.WriteAllText(Path.Combine(scenario.DirectoryPath, "console-buffer-before-exit.txt"), buffer);
        scenario.Release();
        int exit = parentOrProbe.Wait();
        scenario.CompletePipes();
        string finalBuffer = console.Read();
        File.WriteAllText(Path.Combine(scenario.DirectoryPath, "console-buffer-after-exit.txt"), finalBuffer);

        Require(exit == 0 && value.Failure is null, "Probe exit or managed stream write failed. " + value.Failure);
        Require(parentAlive, "Launch process exited before live console evidence.");
        Require(value.ParentProcessId == (spec.Batch ? parentOrProbe.Process.Id : Environment.ProcessId), "Unexpected native parent PID.");
        Require(!value.Entry.HasConsole, "WinExe already had a console on entry.");
        if (!spec.Batch)
            Require(((value.StartupFlags & 0x100) != 0) == spec.StartupHandles, "STARTF_USESTDHANDLES fixture did not match requested launch.");
        CheckSetup(value, spec.Mode);
        Require(value.FirstAttached == spec.Attach, "Engine attach result differed from the native stream contract.");
        Require(!value.SecondAttached, "Second call must consume no further console attachment.");

        bool expectedConsole = spec.Attach || spec.Mode == "already-console";
        Require(value.After.HasConsole == expectedConsole && value.EngineHasConsole == expectedConsole, "Native console ownership or engine HasConsole differed.");
        if (expectedConsole)
        {
            Require(value.After.Window == console.Window, "Attached console window differs from the driver's private console.");
            foreach (int pid in new[] { Environment.ProcessId, value.ParentProcessId, value.ProcessId })
            {
                Require(value.After.ProcessIds.Contains((uint)pid), "Driver, parent and probe did not share a kernel console.");
                if (spec.Mode != "one-shot") Require(liveIds.Contains((uint)pid), "Live kernel console membership missing.");
            }
        }
        if (spec.Mode == "one-shot") Require(!value.AfterSecond.HasConsole, "Spent one-shot reattached after deliberate detach.");
        else Require(value.AfterSecond == value.After || SameStreams(value.AfterSecond, value.After), "Second call changed native stream destinations.");

        if (spec.Mode is "opt-out")
        {
            CheckSilence(finalBuffer, scenario.Marker);
            Require(!value.AfterSecond.HasConsole && SameStreams(value.Before, value.AfterSecond), "Opt-out was not preserved by a later enabled call.");
        }
        else
        {
            CheckStream(value.Before.Output, value.After.Output, "STDOUT", scenario, finalBuffer, spec.Mode,
                expectedConsole, value.NativeOutput);
            CheckStream(value.Before.Error, value.After.Error, "STDERR", scenario, finalBuffer, spec.Mode,
                expectedConsole, value.NativeError);
        }
        if (value.Before.Input.FileType == 1)
        {
            Require(value.After.Input.Handle == value.Before.Input.Handle && value.After.Input.FileType == 1,
                "AttachConsole replaced redirected stdin.");
            Require(value.InputText == Evidence.Input(scenario.Marker), "Native stdin destination did not retain its file marker.");
        }
        if (spec.Mode.StartsWith("fully-", StringComparison.Ordinal))
        {
            Require(!value.After.HasConsole && SameStreams(value.Before, value.After), "Fully redirected process was touched.");
            CheckSilence(finalBuffer, scenario.Marker);
        }
        return $"probe {value.ProcessId}, parent {value.ParentProcessId}, startup flags 0x{value.StartupFlags:x}, native handles and post-exit destinations verified";
    }

    internal string NoParent()
    {
        Require(NativeConsole.GetConsoleWindow() == IntPtr.Zero, "GUI-parent driver must own no console.");
        var spec = new CaseSpec("no-parent-gui-silence", "no-parent", false, Batch: false);
        using var scenario = new Scenario(root, spec);
        using NativeProcess child = NativeProcess.Start(probeExe, scenario.Arguments(spec.Mode));
        ProbeEvidence value = scenario.Read();
        scenario.Release();
        Require(child.Wait() == 0 && value.Failure is null, "No-parent probe failed.");
        Require(value.ParentProcessId == Environment.ProcessId, "No-parent probe had an unexpected native parent.");
        Require(!value.Entry.HasConsole && !value.Before.HasConsole && !value.After.HasConsole
            && !value.AfterSecond.HasConsole && !value.EngineHasConsole && !value.FirstAttached && !value.SecondAttached,
            "Normal GUI launch created or acquired a console.");
        Require(SameStreams(value.Before, value.AfterSecond), "No-parent GUI launch changed native streams.");
        Require(value.NativeOutput is { Success: false } && value.NativeError is { Success: false },
            "Absent GUI streams unexpectedly accepted native terminal markers.");
        return $"probe {value.ProcessId}, console-free GUI parent {value.ParentProcessId}, no console or output destination created";
    }

    private static void CheckSetup(ProbeEvidence value, string mode)
    {
        Require(value.Before.HasConsole == (mode == "already-console"), "Before-attach kernel console fixture differs.");
        if (mode == "already-console") return;
        if (mode.StartsWith("natural-", StringComparison.Ordinal))
        {
            Require(SameStreams(value.Entry, value.Before), "Natural launch changed a native standard handle before the engine call.");
            if (mode == "natural-console-handles")
            {
                Require(value.Before.Output.Handle is not (0 or -1) && value.Before.Error.Handle is not (0 or -1),
                    "Native STARTF launch did not inherit the driver's real console handles.");
                return;
            }
            uint naturalOutput = mode == "natural-stdout-file" ? 1u : 0u;
            uint naturalError = mode == "natural-stderr-file" ? 1u : 0u;
            Require(value.Entry.Output.FileType == naturalOutput && value.Entry.Error.FileType == naturalError,
                "Natural cmd launch did not produce the expected absent/disk native stream types.");
            if (naturalOutput == 0) Require(value.Entry.Output.Handle == 0, "Natural cmd stdout was not absent.");
            if (naturalError == 0) Require(value.Entry.Error.Handle == 0, "Natural cmd stderr was not absent.");
            return;
        }
        long absent = mode == "invalid" ? -1 : 0;
        uint outputType = mode is "stdout-file" or "fully-file" ? 1u : mode is "stdout-pipe" or "fully-pipe" ? 3u : 0u;
        uint errorType = mode is "stderr-file" or "fully-file" ? 1u : mode is "stderr-pipe" or "fully-pipe" ? 3u : 0u;
        Require(value.Before.Output.FileType == outputType && value.Before.Error.FileType == errorType, "Native file/pipe fixture type differs.");
        if (outputType == 0) Require(value.Before.Output.Handle == absent, "stdout absence/invalid fixture differs.");
        if (errorType == 0) Require(value.Before.Error.Handle == absent, "stderr absence/invalid fixture differs.");
    }

    private static void CheckStream(StreamState before, StreamState after, string stream, Scenario scenario,
        string buffer, string mode, bool expectedConsole, WriteResult? native)
    {
        Require(native is { Success: true }, stream + " did not accept its native marker.");
        if (before.FileType is 1 or 3)
        {
            Require(after.Handle == before.Handle && after.FileType == before.FileType && !after.ConsoleMode,
                stream + " redirected native handle was replaced.");
            string name = stream == "STDOUT" ? "stdout" : "stderr";
            string path = Path.Combine(scenario.DirectoryPath, name + (before.FileType == 3 ? "-pipe.txt" : ".txt"));
            Require(File.ReadAllText(path) == Evidence.Expected(scenario.Marker, stream),
                stream + " redirected post-exit content changed, lost a marker or gained a newline.");
            Require(!buffer.Contains(Evidence.Managed(scenario.Marker, stream), StringComparison.Ordinal)
                && !buffer.Contains(Evidence.Native(scenario.Marker, stream), StringComparison.Ordinal),
                stream + " redirected marker leaked into the console.");
        }
        else
        {
            Require(expectedConsole && after.ConsoleMode && after.FileType == 2 && after.Handle is not (0 or -1),
                stream + " was not repaired to a genuine console handle for " + mode);
            Require(buffer.Contains(Evidence.Managed(scenario.Marker, stream), StringComparison.Ordinal)
                && buffer.Contains(Evidence.Native(scenario.Marker, stream), StringComparison.Ordinal),
                stream + " managed/native markers missing from the real terminal buffer.");
        }
    }

    private static bool SameStreams(ConsoleState left, ConsoleState right) =>
        left.Input == right.Input && left.Output == right.Output && left.Error == right.Error && left.Window == right.Window;

    private static void CheckSilence(string buffer, string marker) =>
        Require(!buffer.Contains(marker, StringComparison.Ordinal), "Silent or redirected case leaked a marker into the terminal.");
}
