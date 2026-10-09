namespace WindowsConsoleValidation;

internal sealed record CaseSpec(string Name, string Mode, bool Attach, bool Batch = true, bool StartupHandles = false)
{
    internal static readonly CaseSpec[] Cases =
    [
        new("natural-terminal", "natural-terminal", true),
        new("natural-cmd-stdout-file", "natural-stdout-file", true),
        new("natural-cmd-stderr-file", "natural-stderr-file", true),
        new("natural-inherited-console-handles", "natural-console-handles", true, Batch: false, StartupHandles: true),
        new("absent-both", "absent", true),
        new("invalid-both", "invalid", true),
        new("stdout-file-stderr-absent", "stdout-file", true),
        new("stderr-file-stdout-absent", "stderr-file", true),
        new("stdout-pipe-stderr-absent", "stdout-pipe", true),
        new("stderr-pipe-stdout-absent", "stderr-pipe", true),
        new("fully-redirected-files", "fully-file", false),
        new("fully-redirected-pipes", "fully-pipe", false),
        new("already-console", "already-console", false),
        new("opt-out-one-shot", "opt-out", false),
        new("successful-attach-one-shot", "one-shot", true),
        new("no-startf-stdout-file", "stdout-file", true, Batch: false),
        new("no-startf-stderr-file", "stderr-file", true, Batch: false),
        new("startf-stdout-file", "stdout-file", true, Batch: false, StartupHandles: true),
        new("startf-stderr-file", "stderr-file", true, Batch: false, StartupHandles: true),
        new("startf-invalid-both", "invalid", true, Batch: false, StartupHandles: true)
    ];
}
