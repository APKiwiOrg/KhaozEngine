using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed class RendererPerfVerifiedArtifact
{
    internal RendererPerfReport Report { get; }
    internal RendererPerfCompilerInventory Inventory { get; }
    internal RendererPerfSidecarCounts Counts { get; }
    internal long CombinedBytes { get; }
    RendererPerfVerifiedArtifact(RendererPerfReport report, RendererPerfCompilerInventory inventory,
        RendererPerfSidecarCounts counts, long combinedBytes)
    { Report = report; Inventory = inventory; Counts = counts; CombinedBytes = combinedBytes; }
    internal static RendererPerfVerifiedArtifact Verify(string directory, string reportName, RendererPerfUnitBudget? budget)
    {
        var data = RendererPerfArtifactVerification.Read(directory, reportName, budget);
        return new(data.Report, data.Inventory, data.Counts, data.CombinedBytes);
    }
}

// Same owner for live, received workflow directories, console and offline analysis. No GPU work.
internal static class RendererPerfArtifactVerification
{
    internal sealed record Data(RendererPerfReport Report, RendererPerfCompilerInventory Inventory,
        RendererPerfSidecarCounts Counts, long CombinedBytes);
    internal static RendererPerfVerifiedArtifact Verify(string directory, string reportName, RendererPerfUnitBudget? budget = null)
        => RendererPerfVerifiedArtifact.Verify(directory, reportName, budget);
    internal static Data Read(string directory, string reportName, RendererPerfUnitBudget? budget)
    {
        budget?.Check("renderer.verify.before");
        using var reader = new DirectoryReader(directory, budget);
        var (root, rootLength, _) = reader.Read(reportName, RendererPerfReportJson.MaximumBytes);
        budget?.Check("renderer.verify.root.decode.before");
        var report = RendererPerfReportJson.Parse(root);
        budget?.Check("renderer.verify.root.decode.after");
        if (reportName != report.Manifest.UnitId + ".json") throw new InvalidOperationException("Report filename and unit identity disagree.");
        if (report.Compiler.State != RendererPerfCollectionState.Complete || !report.Publication.FailureText.Complete)
            throw new InvalidOperationException("Incomplete or invalid compiler/failure evidence cannot certify.");
        long combined = rootLength, inventoryBytes = 0;
        RendererPerfCompilerInventory? inventory = null;
        foreach (var expected in report.Sidecars)
        {
            RendererPerfSidecarRegistry.Validate(expected);
            var (bytes, length, hash) = reader.Read(expected.Path, checked((int)expected.ByteLength));
            if (length != expected.ByteLength || hash != expected.Sha256) throw new InvalidOperationException("Sidecar length or SHA256 changed: " + expected.Path);
            if (expected.Kind != RendererPerfSidecarKind.NonCompiler)
            {
                combined = checked(combined + length);
                if (combined > RendererPerfReportJson.MaximumBytes) throw new InvalidOperationException("Root plus compiler files exceed the combined byte cap.");
            }
            budget?.Check("renderer.verify.sidecar.decode.before");
            if (expected.Kind == RendererPerfSidecarKind.CompilerInventory)
            {
                if (inventory is not null || expected.Path != report.Compiler.InventoryPath) throw new InvalidOperationException("Duplicate or unassociated compiler inventory.");
                var file = RendererPerfCompilerJson.ReadFile(bytes, checked((int)length));
                if (file.UnitId != report.Identity.UnitId || file.ManifestSha256 != report.Identity.ManifestSha256 || file.WorkloadSha != report.Identity.WorkloadSha)
                    throw new InvalidOperationException("Compiler file belongs to another unit, manifest or workload.");
                inventory = file.Inventory; inventoryBytes = length;
                var counts = new RendererPerfSidecarCounts(inventory.FactoryEvents.Length, inventory.Occurrences.Length, inventory.Recipes.Length, 0, 0, 0);
                if (expected.Counts != counts) throw new InvalidOperationException("Decoded inventory counts disagree with producer registration.");
            }
            else if (expected.Kind == RendererPerfSidecarKind.CompilerFailure)
                throw new InvalidOperationException("Early-failure evidence cannot substitute for a compiler inventory.");
            else RendererPerfArtifactProducers.Verify(expected, bytes, report);
            budget?.Check("renderer.verify.sidecar.decode.after");
        }
        if (inventory is null || inventory.Recipes.IsEmpty || inventory.Occurrences.IsEmpty) throw new InvalidOperationException("Registered nonempty compiler inventory is missing.");
        var operations = new[] { "conditioning:S3/TotalOnly/0" }.Concat(report.Manifest.Blocks.Select(b => $"{b.SceneId}/{b.Layout}/{b.Iteration}"))
            .Concat(report.Manifest.Controls.Select(c => $"control:{c.Id}/TotalOnly/0"));
        if (!inventory.Operations.SequenceEqual(operations)) throw new InvalidOperationException("Inventory operation membership differs from the frozen manifest.");
        var reasons = RendererPerfCompilerCorrespondence.Validate(inventory, report.Pipelines, report.Compiler);
        if (!reasons.IsEmpty) throw new InvalidOperationException(string.Join(" ", reasons));
        RendererPerfPublicationAccount.Verify(report, inventory, inventoryBytes);
        budget?.Check("renderer.verify.complete");
        return new(report, inventory, new(inventory.FactoryEvents.Length, inventory.Occurrences.Length, inventory.Recipes.Length,
            report.Pipelines.Length, report.Pipelines.Sum(p => p.Executables.Length), report.Pipelines.Sum(p => p.Executables.Sum(e => e.Statistics.Length))), combined);
    }

    // Exclusive handles stay open together. A second path to the same regular file cannot acquire
    // another handle, including hard-link aliases, while the first evidence lease remains held.
    sealed class DirectoryReader(string directory, RendererPerfUnitBudget? budget) : IDisposable
    {
        readonly string _root = System.IO.Path.GetFullPath(directory);
        readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        readonly List<FileStream> _leases = [];
        internal (byte[] Bytes, long Length, string Sha256) Read(string relative, int maximum)
        {
            budget?.Check("renderer.verify.file.before");
            RendererPerfSidecarRegistry.Path(relative);
            if (!_paths.Add(relative)) throw new InvalidOperationException("Duplicate or aliased artifact path.");
            string path = Safe(relative);
            long advertisedLength = new FileInfo(path).Length;
            if (advertisedLength <= 0 || advertisedLength > maximum) throw new InvalidOperationException("Artifact is empty or exceeds its byte capacity.");
            budget?.Check("renderer.verify.file.open.before");
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 65536, FileOptions.SequentialScan);
            _leases.Add(stream);
            long length = stream.Length;
            if (!stream.CanSeek || length != advertisedLength || length <= 0 || length > maximum) throw new InvalidOperationException("Artifact is empty or exceeds its byte capacity.");
            budget?.Check("renderer.verify.file.open.after");
            byte[] result = new byte[checked((int)length)];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int offset = 0;
            while (offset < result.Length)
            {
                budget?.Check("renderer.verify.chunk.before");
                int count = stream.Read(result.AsSpan(offset, Math.Min(65536, result.Length - offset)));
                if (count == 0) throw new EndOfStreamException("Artifact shortened during verification.");
                hash.AppendData(result.AsSpan(offset, count)); offset = checked(offset + count);
                budget?.Check("renderer.verify.chunk.after");
            }
            budget?.Check("renderer.verify.file.end.before");
            if (stream.Length != length || stream.ReadByte() != -1 || Safe(relative) != path || new FileInfo(path).Length != length)
                throw new InvalidOperationException("Artifact changed during verification.");
            budget?.Check("renderer.verify.file.end.after");
            return (result, length, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        string Safe(string relative)
        {
            var root = new DirectoryInfo(_root);
            if (!root.Exists || root.LinkTarget is not null || (root.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Evidence root is missing or traverses a link/reparse point.");
            string path = _root;
            var parts = relative.Split('/');
            for (int n = 0; n < parts.Length; n++)
            {
                path = System.IO.Path.Combine(path, parts[n]);
                FileSystemInfo info = n == parts.Length - 1 ? new FileInfo(path) : new DirectoryInfo(path);
                if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0
                    || n == parts.Length - 1 && (info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                    throw new InvalidOperationException("Expected a regular artifact without link/reparse traversal: " + relative);
            }
            string full = System.IO.Path.GetFullPath(path);
            if (!full.StartsWith(_root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Artifact escaped the evidence root.");
            return full;
        }
        public void Dispose() { foreach (var stream in _leases) stream.Dispose(); }
    }
}
