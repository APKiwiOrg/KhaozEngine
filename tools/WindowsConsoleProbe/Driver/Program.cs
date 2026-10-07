namespace WindowsConsoleValidation;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 2) return 2;
        string root = Path.GetFullPath(args[1]);
        var results = new List<CheckResult>();
        var checks = new ConsoleChecks(Path.GetFullPath(args[0]), root);
        try
        {
            using var console = new ConsoleSession();
            foreach (CaseSpec spec in CaseSpec.Cases) Record(spec.Name, () => checks.Run(console, spec));
        }
        catch (Exception ex) { results.Add(new("private-console-fixture", "FAIL", ex.ToString())); }
        Record("no-parent-gui-silence", checks.NoParent);
        Evidence.Save(Path.Combine(root, "results.json"), new { EngineVersion = Evidence.EngineVersion, Checks = results });
        return results.Any(result => result.Status == "FAIL") ? 1 : 0;

        void Record(string name, Func<string> action)
        {
            try { results.Add(new(name, "PASS", action())); }
            catch (Exception ex) { results.Add(new(name, "FAIL", ex.ToString())); }
        }
    }
}
