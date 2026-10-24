using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed partial class RendererPerfPublicationWriter : IRendererPerfUnitPublication
{
    readonly RendererPerfSidecarRegistry _registry = new();
    RendererPerfPublicationAccount? _account;
    RendererPerfPublicationFiles? _files;
    RendererPerfReport? _prepared;
    RendererPerfCompilerInventory? _inventory;
    bool _preparationAttempted;
    internal void Produce(string path, string schema, byte[] bytes, RendererPerfUnitBudget? budget = null)
    {
        if (directory is null) throw new InvalidOperationException("A producer requires an evidence directory.");
        RendererPerfSidecarRegistry.Path(path);
        RendererPerfPublicationFiles.Atomic(Path.Combine(directory, path), bytes, budget);
        _registry.Register(new(RendererPerfSidecarKind.NonCompiler, schema, path, bytes.Length,
            RendererPerfRecipe.Hash(bytes), RendererPerfSidecarCounts.Empty));
    }
    internal void RegisterProducedFile(string path, string schema, RendererPerfUnitBudget budget)
    {
        if (directory is null) throw new InvalidOperationException("A producer requires an evidence directory.");
        RendererPerfSidecarRegistry.Path(path);
        budget.Check("renderer.publication.producer.open.before");
        using var stream = new FileStream(Path.Combine(directory, path), FileMode.Open, FileAccess.Read, FileShare.None);
        long length = stream.Length, read = 0;
        if (length <= 0 || length > RendererPerfReportJson.MaximumBytes) throw new InvalidOperationException("Producer file is empty or oversized.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] chunk = new byte[65536];
        while (read < length)
        {
            budget.Check("renderer.publication.producer.chunk.before");
            int count = stream.Read(chunk.AsSpan(0, (int)Math.Min(chunk.Length, length - read)));
            if (count == 0) throw new EndOfStreamException("Producer file shortened.");
            read = checked(read + count); hash.AppendData(chunk.AsSpan(0, count));
            budget.Check("renderer.publication.producer.chunk.after");
        }
        if (stream.Length != length || stream.ReadByte() != -1) throw new InvalidOperationException("Producer file changed during registration.");
        budget.Check("renderer.publication.producer.end");
        _registry.Register(new(RendererPerfSidecarKind.NonCompiler, schema, path, length,
            Convert.ToHexStringLower(hash.GetHashAndReset()), RendererPerfSidecarCounts.Empty));
    }
    internal void RegisterExisting(string path, string schema, byte[] producedBytes)
    {
        RendererPerfSidecarRegistry.Path(path);
        _registry.Register(new(RendererPerfSidecarKind.NonCompiler, schema, path, producedBytes.Length,
            RendererPerfRecipe.Hash(producedBytes), RendererPerfSidecarCounts.Empty));
    }
    public RendererPerfCollectionPreparation PrepareCollection(RendererPerfReport report, RendererPerfCompilerInventory inventory,
        RendererPerfUnitBudget budget)
    {
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        try
        {
            if (_preparationAttempted) throw new InvalidOperationException("Collection preparation is unit-scoped and cannot repeat.");
            _preparationAttempted = true;
            budget.Check("renderer.publication.prepare.before");
            if (!report.Pipelines.IsEmpty) throw new InvalidOperationException("Precollection report already contains native results.");
            RendererPerfCompilerJson.Validate(inventory);
            if (directory is not null) Produce("work-models.json", RendererPerfArtifactProducers.Schema("work-models.json"),
                RendererPerfArtifactProducers.WorkModels(report), budget);
            _prepared = Attach(prepare?.Invoke(report) ?? report, inventory, budget, prepareCollection: true);
            budget.Check("renderer.publication.prepare.after");
            return new(true, []);
        }
        catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.publication.prepare", error); return new(false, failures.ToImmutable()); }
    }
    internal ImmutableArray<RendererPerfExpectedSidecar> ExpectedSidecars => _registry.Freeze();
    long _inventoryLength;
    RendererPerfReport Attach(RendererPerfReport report, RendererPerfCompilerInventory inventory, RendererPerfUnitBudget? budget, bool prepareCollection = false)
    {
        if (_inventory is not null)
        {
            if (!ReferenceEquals(_inventory, inventory)) throw new InvalidOperationException("Publication substituted the frozen inventory instance.");
            return report with { Sidecars = _registry.Freeze(), Compiler = report.Compiler with { InventoryPath = report.Manifest.UnitId + "-compiler-inventory.json" } };
        }
        _inventory = inventory;
        string path = report.Manifest.UnitId + "-compiler-inventory.json";
        byte[] bytes = RendererPerfCompilerJson.Encode(new RendererPerfCompilerFile(RendererPerfCompilerJson.Schema,
            report.Identity.UnitId, report.Identity.ManifestSha256, report.Identity.WorkloadSha, inventory), RendererPerfReportJson.MaximumBytes);
        _inventoryLength = bytes.Length;
        _registry.Register(new(RendererPerfSidecarKind.CompilerInventory, RendererPerfCompilerJson.Schema, path, bytes.Length,
            RendererPerfRecipe.Hash(bytes), new(inventory.FactoryEvents.Length, inventory.Occurrences.Length, inventory.Recipes.Length, 0, 0, 0)));
        var attached = report with { Sidecars = _registry.Freeze(), Compiler = report.Compiler with { InventoryPath = path } };
        // Serialized bytes are producer output, but they are not written until the combined account
        // admits them. A refused preparation keeps the detached inventory and expected metadata.
        if (prepareCollection)
        {
            _account = new(attached, inventory, _inventoryLength, budget!);
            if (!inventory.Recipes.IsEmpty && attached.Compiler.Production is { } configuration
                && RendererPerfCompilerReadContract.ForAvailable(configuration.Backend, _account.Available) is null)
                throw new InvalidOperationException("Compiler report capacity refused before diagnostic creation.");
        }
        else
        {
            long rootBytes = RendererPerfRawJson.Serialize(attached, RendererPerfReportJson.MaximumBytes).LongLength;
            if (checked(rootBytes + bytes.LongLength + 64) > RendererPerfReportJson.MaximumBytes)
                throw new InvalidOperationException("Retained root and compiler inventory exceed the combined publication capacity.");
        }
        if (directory is not null) RendererPerfPublicationFiles.Atomic(Path.Combine(directory, path), bytes, budget);
        return attached;
    }
    public RendererPerfReadReservation? ReserveReport(GpuBackendKind backend)
        => (_account ?? throw new InvalidOperationException("Collection was not prepared.")).ReserveReport(backend);
    public void CommitReportBytes(RendererPerfReadReservation reservation, int actualBytes)
        => (_account ?? throw new InvalidOperationException("Collection was not prepared.")).Commit(reservation, actualBytes);
    public RendererPerfPublicationResult Publish(RendererPerfReport report, RendererPerfCompilerInventory inventory,
        RendererPerfUnitBudget budget)
    {
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        bool finalAttempted = false;
        string? priorOriginal = report.Publication.OriginalFailure;
        var priorErrors = report.Publication.Errors;
        var priorText = report.Publication.FailureText;
        _files?.SetBudget(budget);
        try
        {
            report = Attach(report, inventory, report.Publication.OriginalFailure is null ? budget : null);
            if (_prepared is not null) report = report with { Assessment = report.Assessment with { WorkModels = _prepared.Assessment.WorkModels } };
            bool failed = report.Publication.OriginalFailure is not null || report.Compiler.State != RendererPerfCollectionState.Complete;
            _account?.Finish(failed);
            report = report with { Compiler = report.Compiler with { Accounting = _account?.Snapshot } };
            if (priorOriginal is not null) report = RendererPerfPublicationFailureText.Project(report, priorOriginal, priorErrors,
                priorText, [], Math.Max(0, RendererPerfPublicationAccount.CompletionReserve - (_account?.Snapshot.CompletionShapeBytes ?? 0)));
            // Initial partial is unconditional recovery evidence, including when the original budget expired.
            var partial = Failure(Update(report), report.Publication.OriginalFailure ?? "Final publication pending.", null);
            write(partial, false);
            if (report.Publication.OriginalFailure is not null) return new(partial, []);
            if (failed || _account is null) throw new InvalidOperationException("Incomplete collection or missing admitted publication account.");
            budget.Check("renderer.publication.final.before");
            report = Update(report);
            report = report with
            { Publication = report.Publication with { State = RendererPerfPublicationState.Completed, ReturnedByRun = true, FinalUnitPublished = true } };
            finalAttempted = true;
            write(report, true);
            budget.Check("renderer.publication.final.after");
            if (directory is null) throw new InvalidOperationException("File verification is required before completion.");
            _ = RendererPerfArtifactVerification.Verify(directory, report.Manifest.UnitId + ".json", budget);
            budget.Check("renderer.publication.verify.after");
            long completedAt = budget.Clock.Now();
            report = Update(report, completedAt);
            budget.Check("renderer.publication.stamp.before");
            recordCompletion(report);
            budget.Check("renderer.publication.stamp.after");
            // This is Unit's terminal enforcement. It must share the final correction/recovery
            // owner because the successful stamp already exists on disk at this boundary.
            budget.Check("renderer.publication.return");
            return new(report, []);
        }
        catch (Exception error)
        {
            RendererPerfFailures.Add(failures, "renderer.publication.final", error);
            try { report = Update(report); }
            catch (Exception clock) { RendererPerfFailures.Add(failures, "renderer.publication.final", clock); }
            report = Project(report, failures.ToImmutable());
            if (finalAttempted)
            {
                try { write(report, true); }
                catch (Exception correction) { RendererPerfFailures.Add(failures, "renderer.publication.correction", correction); report = Project(report, failures.ToImmutable()); }
            }
            try { write(report, false); }
            catch (Exception recovery) { RendererPerfFailures.Add(failures, "renderer.publication.recovery", recovery); report = Project(report, failures.ToImmutable()); }
            try { report = Update(report); }
            catch (Exception clock) { RendererPerfFailures.Add(failures, "renderer.publication.recovery", clock); report = Project(report, failures.ToImmutable()); }
            return new(report, failures.ToImmutable());
        }
        RendererPerfReport Update(RendererPerfReport value, long? instant = null)
        {
            double elapsed = checked((instant ?? budget.Clock.Now()) - budget.ColdStart) * 1000d / budget.Clock.Frequency;
            return value with
            {
                Publication = value.Publication with
                {
                    ElapsedMilliseconds = RendererPerfDriver.Value(elapsed, "ms", "unit monotonic clock including publication"),
                    EstimatedCostUsd = RendererPerfDriver.Value(elapsed / 60000d * (double)value.Manifest.Budget.RatePerMinute, "USD", "unit elapsed running time estimate")
                }
            };
        }
        RendererPerfReport Project(RendererPerfReport value, ImmutableArray<GpuDiagnosticFailure> added)
        {
            _account?.Finish(true);
            value = value with { Compiler = value.Compiler with { Accounting = _account?.Snapshot } };
            return RendererPerfPublicationFailureText.Project(value, priorOriginal, priorErrors,
                priorText, added, Math.Max(0, RendererPerfPublicationAccount.CompletionReserve - (_account?.Snapshot.CompletionShapeBytes ?? 0)));
        }
    }
    public RendererPerfFailurePublicationResult WriteFailure(RendererPerfUnitFailure failure, RendererPerfUnitBudget budget)
    {
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        bool written = false;
        try { Write(false); written = true; }
        catch (Exception error)
        {
            RendererPerfFailures.Add(failures, "renderer.publication.early.write", error);
            try { Write(true); written = true; }
            catch (Exception recovery) { RendererPerfFailures.Add(failures, "renderer.publication.early.recovery", recovery); }
        }
        return new(failure, written, failures.ToImmutable());
        void Write(bool recovery)
        {
            byte[] bytes = RendererPerfCompilerJson.Encode(failure, RendererPerfPublicationAccount.CompletionReserve);
            string path = failure.Authorization.ManifestSha256 + (recovery ? ".failure.partial.json" : ".failure.json");
            if (writeFailure is not null) writeFailure(path, bytes);
            else
            {
                if (directory is null) throw new InvalidOperationException("Early failure requires an evidence directory.");
                RendererPerfPublicationFiles.Atomic(Path.Combine(directory, path), bytes, null);
            }
            _registry.Register(new(RendererPerfSidecarKind.CompilerFailure, failure.Schema, path, bytes.Length,
                RendererPerfRecipe.Hash(bytes), RendererPerfSidecarCounts.Empty));
        }
    }
}
