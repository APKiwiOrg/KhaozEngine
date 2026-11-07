using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registered_files_are_reopened_and_reconciled_before_certification(bool unsupported)
    {
        using var f = new RendererPerfCompilerPublicationFixture(unsupported: unsupported);
        var result = f.Publish(RendererPerfPublicationWriter.Files(f.Directory));
        Assert.Empty(result.Failures);
        Assert.True(result.Report.Publication.FinalUnitPublished, result.Report.Publication.OriginalFailure);
        var verified = RendererPerfArtifactVerification.Verify(f.Directory, f.Report.Manifest.UnitId + ".json");
        Assert.Equal(0, RunConsole(f.Directory, f.Report.Manifest.UnitId + ".json").Exit);
        Assert.Equal(2, verified.Counts.Reports);
        Assert.Equal(27, verified.Counts.Occurrences);
        Assert.Equal(unsupported ? 0 : 2, verified.Counts.Executables);
        Assert.Equal(unsupported ? 0 : 10, verified.Counts.Statistics);
        Assert.Equal(verified.Report.Sidecars.Where(s => s.Kind != RendererPerfSidecarKind.NonCompiler).Sum(s => s.ByteLength) + new FileInfo(Path.Combine(f.Directory, f.Report.Manifest.UnitId + ".json")).Length,
            verified.CombinedBytes);
        var restored = RendererPerfCompilerJson.Parse(RendererPerfCompilerJson.Serialize(f.Inventory, 67108864), 67108864);
        Assert.Equal(f.Inventory.Recipes.Select(r => r.RecipeSha256), restored.Recipes.Select(r => r.RecipeSha256));
        Assert.Equal(f.Inventory.Counts, restored.Counts);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("changed")]
    [InlineData("changed-same-length")]
    [InlineData("wrong-unit")]
    [InlineData("wrong-kind")]
    [InlineData("duplicate")]
    [InlineData("alias")]
    [InlineData("escaped")]
    [InlineData("symlink")]
    [InlineData("counts")]
    [InlineData("occurrence")]
    [InlineData("stage")]
    [InlineData("request")]
    [InlineData("substituted-request")]
    [InlineData("commit")]
    [InlineData("account-base")]
    [InlineData("report1")]
    [InlineData("unknown-field")]
    [InlineData("missing-field")]
    [InlineData("duplicate-field")]
    [InlineData("wrong-schema")]
    [InlineData("symlink-directory")]
    public void Missing_changed_aliased_empty_or_wrong_identity_sidecars_refuse(string fault)
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        var published = f.Publish(RendererPerfPublicationWriter.Files(f.Directory));
        Assert.Empty(published.Failures);
        string name = f.Report.Manifest.UnitId + ".json", root = Path.Combine(f.Directory, name);
        var json = JsonNode.Parse(File.ReadAllBytes(root))!;
        int slot = json["sidecars"]!.AsArray().Select((node, index) => (node, index)).Single(p => p.node!["kind"]!.GetValue<string>() == "compiler-inventory").index;
        string path = Path.Combine(f.Directory, json["sidecars"]![slot]!["path"]!.GetValue<string>());
        switch (fault)
        {
            case "missing": File.Delete(path); break;
            case "empty": File.WriteAllBytes(path, []); break;
            case "changed": File.AppendAllText(path, " "); break;
            case "changed-same-length":
                byte[] changed = File.ReadAllBytes(path); changed[^3] ^= 1; File.WriteAllBytes(path, changed); break;
            case "symlink" when !OperatingSystem.IsWindows():
                File.Move(path, path + ".original"); File.CreateSymbolicLink(path, path + ".original"); break;
            case "symlink":
            case "symlink-directory":
                string target = Path.Combine(f.Directory, "original"); System.IO.Directory.CreateDirectory(target);
                File.Move(path, Path.Combine(target, Path.GetFileName(path)));
                string link = Path.Combine(f.Directory, "linked");
                if (OperatingSystem.IsWindows()) Link("/J", link, target);
                else System.IO.Directory.CreateSymbolicLink(link, target);
                json["sidecars"]![slot]!["path"] = "linked/" + Path.GetFileName(path);
                json["compiler"]!["inventoryPath"] = "linked/" + Path.GetFileName(path);
                break;
            case "unknown-field": RewriteInventory(node => node["inventory"]!["extra"] = 1); break;
            case "missing-field": RewriteInventory(node => node["inventory"]!.AsObject().Remove("counts")); break;
            case "wrong-schema": RewriteInventory(node => node["schema"] = "renderer-source-corpus/1"); break;
            case "duplicate-field":
                string duplicate = File.ReadAllText(path).Replace("\"schema\":", "\"schema\":\"duplicate\",\"schema\":", StringComparison.Ordinal);
                File.WriteAllText(path, duplicate); UpdateReference(); break;
            case "wrong-unit":
                var sidecar = JsonNode.Parse(File.ReadAllBytes(path))!; sidecar["unitId"] = "another-unit";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(sidecar.ToJsonString()); File.WriteAllBytes(path, bytes);
                json["sidecars"]![slot]!["byteLength"] = bytes.Length; json["sidecars"]![slot]!["sha256"] = RendererPerfRecipe.Hash(bytes); break;
            case "wrong-kind": json["sidecars"]![slot]!["kind"] = "non-compiler"; break;
            case "duplicate": json["sidecars"]!.AsArray().Add(json["sidecars"]![slot]!.DeepClone()); break;
            case "alias": json["sidecars"]![slot]!["path"] = name; break;
            case "escaped": json["sidecars"]![slot]!["path"] = "../escape.json"; break;
            case "counts": json["sidecars"]![slot]!["counts"]!["occurrences"] = 26; break;
            case "occurrence": json["compiler"]!["results"]![0]!["occurrenceIds"]!.AsArray().RemoveAt(0); break;
            case "stage": json["pipelines"]![0]!["identity"]!["stages"]![0]!["sourceSha256"]!["sha256"] = new string('e', 64); break;
            case "request": json["compiler"]!["results"]![0]!["read"]!["request"]!["maximumTotalStatistics"] = 1; break;
            case "substituted-request":
                var request = json["compiler"]!["results"]![0]!["read"]!["request"]!;
                request["maximumTotalStatistics"] = request["maximumTotalStatistics"]!.GetValue<int>() - 1; break;
            case "commit":
                var admission = json["compiler"]!["accounting"]!["admissions"]![0]!;
                admission["committedBytes"] = admission["committedBytes"]!.GetValue<int>() + 1; break;
            case "account-base":
                var account = json["compiler"]!["accounting"]!;
                account["baseRootBytes"] = account["baseRootBytes"]!.GetValue<long>() + 1; break;
            case "report1": json["schema"] = "khaozengine.renderer-perf-report/1"; break;
        }
        File.WriteAllText(root, json.ToJsonString());
        Assert.ThrowsAny<Exception>(() => RendererPerfArtifactVerification.Verify(f.Directory, name));
        Assert.NotEqual(0, RunConsole(f.Directory, name).Exit);
        void RewriteInventory(Action<JsonNode> change)
        {
            var node = JsonNode.Parse(File.ReadAllBytes(path))!; change(node); File.WriteAllText(path, node.ToJsonString()); UpdateReference();
        }
        void UpdateReference()
        {
            byte[] bytes = File.ReadAllBytes(path);
            json["sidecars"]![slot]!["byteLength"] = bytes.Length;
            json["sidecars"]![slot]!["sha256"] = RendererPerfRecipe.Hash(bytes);
        }
    }

    [Fact]
    public void Hard_link_aliases_refuse_before_the_second_decode()
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        Assert.Empty(f.Publish(RendererPerfPublicationWriter.Files(f.Directory)).Failures);
        string name = f.Report.Manifest.UnitId + ".json", path = Path.Combine(f.Directory, name);
        string alias = Path.Combine(f.Directory, "alias.json");
        var node = JsonNode.Parse(File.ReadAllBytes(path))!;
        var sidecar = node["sidecars"]!.AsArray().Single(n => n!["kind"]!.GetValue<string>() == "compiler-inventory")!;
        sidecar["path"] = "alias.json"; sidecar["byteLength"] = 268435456;
        node["compiler"]!["inventoryPath"] = "alias.json";
        File.WriteAllText(path, node.ToJsonString());
        Link("/H", alias, path);
        Assert.Throws<IOException>(() => RendererPerfArtifactVerification.Verify(f.Directory, name));
        Assert.NotEqual(0, RunConsole(f.Directory, name).Exit);
    }
    void Link(string kind, string link, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd" : "ln")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in OperatingSystem.IsWindows() ? new[] { "/c", "mklink", kind, link, target } : new[] { target, link }) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        string stdout = process.StandardOutput.ReadToEnd(), stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        _output.WriteLine("link argv={0} {1} cwd={2} exit={3} stdout={4} stderr={5}", info.FileName, string.Join(" ", info.ArgumentList),
            Environment.CurrentDirectory, process.ExitCode, stdout, stderr);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Reservation_instance_commits_are_checked_and_retained_once()
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        var writer = RendererPerfPublicationWriter.Files(f.Directory);
        Assert.True(writer.PrepareCollection(f.Report, f.Inventory, f.Budget).Ready);
        var q = writer.ReserveReport(KhaozEngine.Gpu.GpuBackendKind.VulkanNative)!;
        Assert.Throws<InvalidOperationException>(() => writer.ReserveReport(KhaozEngine.Gpu.GpuBackendKind.VulkanNative));
        Assert.Throws<InvalidOperationException>(() => writer.CommitReportBytes(q with { }, 1));
        Assert.Throws<InvalidOperationException>(() => writer.CommitReportBytes(q, -1));
        Assert.Throws<InvalidOperationException>(() => writer.CommitReportBytes(q, q.MaximumEncodedBytes + 1));
        writer.CommitReportBytes(q, 100);
        Assert.Throws<InvalidOperationException>(() => writer.CommitReportBytes(q, 100));
    }
}
