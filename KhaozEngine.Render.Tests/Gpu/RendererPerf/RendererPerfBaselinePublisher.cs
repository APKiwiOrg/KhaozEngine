using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfBaselineLeg(string Leg, string Directory, string ReportName, string RunUrl,
    int BilledMinutes, string ManifestSha256, string WorkloadSha, string WorkflowSha, string HarnessSha256,
    ImmutableDictionary<string, RendererPerfReferenceRates>? References = null);
internal sealed record RendererPerfBaselineRequest(string Schema, string OutputDirectory, ImmutableArray<RendererPerfBaselineLeg> Legs);
internal sealed class RendererPerfBaselineException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    internal string Code { get; } = code;
}

// Offline admission owns the complete pair before exclusive output creation. Never a parse-only path.
internal static class RendererPerfBaselinePublisher
{
    internal static ImmutableArray<string> PublishFile(string requestPath)
    {
        RendererPerfBaselineRequest request;
        try
        {
            string path = Path.GetFullPath(requestPath);
            string parent = Path.GetDirectoryName(path)!;
            using var stream = File.OpenRead(path);
            if (stream.Length is <= 0 or > 1048576) throw new InvalidDataException("Publication request exceeds its 1 MiB capacity or is empty.");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Publication request changed length.");
            request = RendererPerfManifest.ReadJson<RendererPerfBaselineRequest>(bytes, 1048576);
            request = request with
            {
                OutputDirectory = Path.GetFullPath(request.OutputDirectory, parent),
                Legs = [.. request.Legs.Select(l => l with { Directory = Path.GetFullPath(l.Directory, parent) })]
            };
        }
        catch (Exception error) { throw new RendererPerfBaselineException("Request", "Cannot read the bounded publication request.", error); }
        return Publish(request);
    }

    internal static ImmutableArray<string> Publish(RendererPerfBaselineRequest request)
    {
        Require(request.Schema == "khaozengine.renderer-baseline-request/1" && !string.IsNullOrWhiteSpace(request.OutputDirectory),
            "Request", "Expected renderer-baseline-request/1 and an explicit output directory.");
        Require(!request.Legs.IsDefault && request.Legs.Length == 2, "Pair", "Exactly two primary full legs are required.");
        var verified = new List<(RendererPerfBaselineLeg Input, RendererPerfSummary Summary)>();
        foreach (var input in request.Legs.OrderBy(l => l.Leg, StringComparer.Ordinal))
        {
            RendererPerfVerifiedArtifact artifact;
            try { artifact = RendererPerfArtifactVerification.Verify(input.Directory, input.ReportName); }
            catch (Exception error) { throw new RendererPerfBaselineException("EvidenceInvalid", "Received directory verification refused.", error); }
            var report = artifact.Report;
            Require(report.Manifest.ExecutionClass == RendererPerfExecutionClass.Performance, "Rehearsal", "Software rehearsal cannot supply a baseline.");
            Require(report.Manifest.Preset == "full", "NotFull", "Only a complete full schedule supplies a baseline.");
            var identity = report.Identity;
            Require(input.Leg is "linux-vulkan" or "windows-d3d11" && input.Leg == report.Manifest.Execution.Leg
                && input.ManifestSha256 == identity.ManifestSha256 && input.WorkloadSha == identity.WorkloadSha
                && input.WorkflowSha == identity.WorkflowSha && input.HarnessSha256 == identity.HarnessSha256,
                "Identity", "Received leg or expected manifest, source, workflow or harness identity differs.");
            Require(identity.RunId.Length != 0 && identity.RunId.All(c => c is >= '0' and <= '9')
                && input.RunUrl == $"https://github.com/APKiwiOrg/KhaozEngine/actions/runs/{identity.RunId}/attempts/{identity.Attempt}",
                "Identity", "Run URL must identify this repository's exact run and attempt.");
            Require(report.Compiler.Production is { } configuration && configuration.Driver == identity.Driver
                && configuration.Backend == (input.Leg == "linux-vulkan" ? GpuBackendKind.VulkanNative : GpuBackendKind.Direct3D11Native),
                "Identity", "Compiler backend or driver differs from the native run identity.");
            try { RendererPerfAdmission.Identity(report.Manifest, identity); }
            catch (Exception error) { throw new RendererPerfBaselineException("Identity", "Native admission refused the received identity.", error); }
            var summary = RendererPerfAnalysis.Analyze(artifact, input.References);
            Require(summary.PromotionEligible, "Promotion", string.Join(" ", summary.Reasons));
            Require(input.BilledMinutes > 0 && input.BilledMinutes <= report.Manifest.Budget.MaximumBilledMinutes,
                "Billing", "Actual billed minutes must be explicit, positive and within the approved cap.");
            verified.Add((input, summary));
        }
        Require(verified[0].Input.Leg == "linux-vulkan" && verified[1].Input.Leg == "windows-d3d11",
            "Pair", "One Linux Vulkan and one Windows D3D11 unit are required.");
        var a = verified[0].Summary.Raw.Identity;
        var b = verified[1].Summary.Raw.Identity;
        Require(a.WorkloadSha == b.WorkloadSha && a.BaselineMainSha == b.BaselineMainSha,
            "Pair", "The pair must share the frozen workload and baseline source.");
        var files = verified.Select(v => (Name: v.Input.Leg + "-" + v.Summary.Raw.Identity.UnitId + ".md",
            Text: RendererPerfBaselineMarkdown.Summary(v.Input, v.Summary))).ToList();
        files.Add(($"index-{a.UnitId}-{b.UnitId}.md", RendererPerfBaselineMarkdown.Index(verified)));
        Require(files.All(f => !Path.Exists(Path.Combine(request.OutputDirectory, f.Name))), "Exists", "An immutable summary or index already exists.");
        try
        {
            Directory.CreateDirectory(request.OutputDirectory);
            FileStream Open(int fileIndex) => new(Path.Combine(request.OutputDirectory, files[fileIndex].Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var linux = Open(0);
            using var windows = Open(1);
            using var indexFile = Open(2);
            var encoding = new UTF8Encoding(false, true);
            linux.Write(encoding.GetBytes(files[0].Text));
            windows.Write(encoding.GetBytes(files[1].Text));
            indexFile.Write(encoding.GetBytes(files[2].Text));
        }
        catch (Exception error) { throw new RendererPerfBaselineException("Write", "Exclusive baseline publication failed. Preserve any partial output.", error); }
        return [.. files.Select(f => f.Name)];
    }

    static void Require(bool condition, string code, string reason)
    { if (!condition) throw new RendererPerfBaselineException(code, reason); }
}
