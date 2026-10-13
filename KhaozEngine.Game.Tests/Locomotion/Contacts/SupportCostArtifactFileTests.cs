using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed class SupportCostArtifactFileTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("write")]
    [InlineData("replace")]
    public void FinalizerClosesTemporaryFileBeforeReplacementOrFailureCleanup(string fault)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cost.json");
        try
        {
            var artifact = new JsonObject
            {
                ["baseline"] = new JsonObject
                {
                    ["RunId"] = "file-lifetime",
                    ["CapturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["ProcessExitStatus"] = null,
                    ["Samples"] = new JsonArray(new JsonObject { ["Scene"] = "fixture" })
                },
                ["pendingFinalization"] = new JsonObject
                {
                    ["Section"] = "baseline",
                    ["RunId"] = "file-lifetime"
                }
            };
            string input = artifact.ToJsonString();
            // Python reads NaN, but the finalizer refuses to serialize it into the replacement.
            if (fault == "write") input = input[..^1] + ",\"NonFinite\":NaN}";
            File.WriteAllText(path, input);
            byte[] before = File.ReadAllBytes(path);

            (int status, string error) = FinalizeWithFileLifetimeGuard(path, fault);

            Assert.True(status == (fault == "none" ? 0 : 1), error);
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
            if (fault != "none")
            {
                Assert.Equal(before, File.ReadAllBytes(path));
                return;
            }
            Assert.Equal("", error);
            JsonNode finalized = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(23, finalized["baseline"]!["ProcessExitStatus"]!.GetValue<int>());
            Assert.NotNull(finalized["baseline"]!["FinalizedAtUtc"]);
            Assert.Equal("fixture", finalized["baseline"]!["Samples"]![0]!["Scene"]!.GetValue<string>());
            Assert.Null(finalized["pendingFinalization"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    static (int Status, string Error) FinalizeWithFileLifetimeGuard(string artifact, string fault)
    {
        string? root = Directory.GetCurrentDirectory();
        while (root is not null && !File.Exists(Path.Combine(root, "scripts", "record-support-cost-exit.py")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var info = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardError = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("""
            import os
            from pathlib import Path
            import runpy
            import sys
            import tempfile

            finalize = runpy.run_path(sys.argv[1])["finalize"]
            original_temporary_file = tempfile.NamedTemporaryFile
            original_replace = os.replace
            original_unlink = os.unlink
            handles = []

            def temporary_file(*args, **kwargs):
                handle = original_temporary_file(*args, **kwargs)
                handles.append(handle)
                return handle

            def require_closed(path):
                for handle in handles:
                    if str(path) == handle.name and not handle.closed:
                        raise PermissionError(32, "Temporary file is still open", str(path))

            def replace(source, destination):
                require_closed(source)
                if sys.argv[3] == "replace":
                    destination = Path(destination).parent / "missing" / Path(destination).name
                return original_replace(source, destination)

            def unlink(path, *args, **kwargs):
                require_closed(path)
                return original_unlink(path, *args, **kwargs)

            # Enforce Windows file lifetime rules while keeping real handles and disk operations.
            tempfile.NamedTemporaryFile = temporary_file
            os.replace = replace
            os.unlink = unlink
            try:
                finalize(Path(sys.argv[2]), 23)
            except (OSError, ValueError) as error:
                sys.exit(str(error))
            """);
        info.ArgumentList.Add(Path.Combine(root!, "scripts", "record-support-cost-exit.py"));
        info.ArgumentList.Add(artifact);
        info.ArgumentList.Add(fault);
        using Process process = Process.Start(info)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error);
    }
}
