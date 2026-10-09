namespace WindowsConsoleValidation;

internal sealed class Scenario : IDisposable
{
    private readonly EventWaitHandle ready;
    private readonly EventWaitHandle release;
    private readonly PipeCapture? outputPipe;
    private readonly PipeCapture? errorPipe;
    internal string DirectoryPath { get; }
    internal string Marker { get; } = Guid.NewGuid().ToString("N");
    internal string EventPrefix { get; }

    internal Scenario(string root, CaseSpec spec)
    {
        DirectoryPath = Path.Combine(root, spec.Name);
        Directory.CreateDirectory(DirectoryPath);
        EventPrefix = "Local\\KE-WindowsConsole-" + Marker;
        ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventPrefix + "-ready");
        release = new EventWaitHandle(false, EventResetMode.ManualReset, EventPrefix + "-release");
        if (spec.Mode is "stdout-pipe" or "fully-pipe") outputPipe = new PipeCapture();
        if (spec.Mode is "stderr-pipe" or "fully-pipe") errorPipe = new PipeCapture();
    }

    internal string Arguments(string mode) => string.Join(" ", new[]
    {
        mode, DirectoryPath, Marker, EventPrefix, (outputPipe?.Handle ?? 0).ToString(), (errorPipe?.Handle ?? 0).ToString()
    }.Select(NativeProcess.Quote));

    internal ProbeEvidence Read()
    {
        if (!ready.WaitOne(TimeSpan.FromSeconds(20))) throw new TimeoutException("Probe produced no ready evidence within 20 seconds.");
        ProbeEvidence result = Evidence.Read<ProbeEvidence>(Path.Combine(DirectoryPath, "evidence.json"));
        ConsoleChecks.Require(result.Marker == Marker && result.EngineVersion == Evidence.EngineVersion,
            "Evidence identity or engine source version differs.");
        return result;
    }

    internal void Release() => release.Set();

    internal void CompletePipes()
    {
        if (outputPipe is not null) File.WriteAllText(Path.Combine(DirectoryPath, "stdout-pipe.txt"), outputPipe.Complete());
        if (errorPipe is not null) File.WriteAllText(Path.Combine(DirectoryPath, "stderr-pipe.txt"), errorPipe.Complete());
    }

    public void Dispose()
    {
        Release();
        try { outputPipe?.Dispose(); }
        finally
        {
            try { errorPipe?.Dispose(); }
            finally { ready.Dispose(); release.Dispose(); }
        }
    }
}
