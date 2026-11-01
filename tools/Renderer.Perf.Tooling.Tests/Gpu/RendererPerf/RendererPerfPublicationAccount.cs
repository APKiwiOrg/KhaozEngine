using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfCompilerAdmission(int RecipeId, GpuPipelineCompilerReadRequest Request,
    int MaximumEncodedBytes, int? CommittedBytes);
internal sealed record RendererPerfCompilerAccounting(long BaseRootBytes, long InventoryBytes, int CompletionShapeBytes,
    ImmutableArray<RendererPerfCompilerAdmission> Admissions);

// Native reports remain charged until terminal publication. No release operation discards retained evidence.
internal sealed class RendererPerfPublicationAccount
{
    internal const int CompletionReserve = 16777216;
    readonly List<RendererPerfCompilerAdmission> _admissions = [];
    RendererPerfReadReservation? _outstanding;
    long _committed;
    readonly long _baseRoot, _inventory;
    readonly int _shape, _recipes;
    readonly RendererPerfUnitBudget _budget;
    internal RendererPerfPublicationAccount(RendererPerfReport report, RendererPerfCompilerInventory inventory,
        long inventoryBytes, RendererPerfUnitBudget budget)
    {
        _budget = budget; _inventory = inventoryBytes; _recipes = inventory.Recipes.Length;
        budget.Check("renderer.publication.account.before");
        _baseRoot = BaseBytes(report, inventory);
        _shape = CompletionBytes(report, inventory);
        if (_shape > CompletionReserve || Available < 0) throw new InvalidOperationException("Retained base/inventory and completion evidence exceed publication capacity.");
        budget.Check("renderer.publication.account.after");
    }
    internal long Available => checked(RendererPerfReportJson.MaximumBytes - _baseRoot - _inventory - _committed - CompletionReserve);
    internal RendererPerfCompilerAccounting Snapshot => new(_baseRoot, _inventory, _shape, [.. _admissions]);
    internal RendererPerfReadReservation? Reserve(GpuBackendKind backend)
    {
        _budget.Check("renderer.publication.reserve");
        if (_outstanding is not null) throw new InvalidOperationException("A report reservation is already outstanding.");
        if (_admissions.Count >= _recipes) throw new InvalidOperationException("Every inventory recipe already has a reservation.");
        var reservation = RendererPerfCompilerReadContract.ForAvailable(backend, Available);
        if (reservation is null) return null;
        _outstanding = reservation;
        _admissions.Add(new(_admissions.Count, reservation.Request, reservation.MaximumEncodedBytes, null));
        return reservation;
    }
    internal void Commit(RendererPerfReadReservation reservation, int bytes)
    {
        if (_terminal || !ReferenceEquals(reservation, _outstanding) || bytes < 0 || bytes > reservation.MaximumEncodedBytes)
            throw new InvalidOperationException("Commit requires the outstanding reservation instance and a nonnegative bounded byte count.");
        _committed = checked(_committed + bytes);
        _admissions[^1] = _admissions[^1] with { CommittedBytes = bytes };
        _outstanding = null;
    }
    internal void Finish(bool failed)
    {
        if (_outstanding is not null && !failed) throw new InvalidOperationException("An uncommitted native report requires terminal refusal.");
        // A failed publication retains the uncommitted request in Snapshot. It cannot admit another report.
        _terminal = true;
    }
    bool _terminal;
    internal RendererPerfReadReservation? ReserveReport(GpuBackendKind backend)
        => _terminal ? throw new InvalidOperationException("Publication is terminal.") : Reserve(backend);

    internal static long BaseBytes(RendererPerfReport report, RendererPerfCompilerInventory inventory)
    {
        var compiler = new RendererPerfCompilerEvidence(RendererPerfCollectionState.Incomplete, "Cold collection has not completed.",
            inventory.Counts, report.Compiler.Production, null,
            [.. inventory.Recipes.Select(r => new RendererPerfCompilerResult(r.Id, RendererPerfCollectionState.Incomplete,
                "Not attempted.", null, [.. inventory.Occurrences.Where(o => o.RecipeId == r.Id).Select(o => o.Id)], null))],
            report.Compiler.InventoryPath, [], []);
        var normalized = report with
        {
            Pipelines = [],
            Compiler = compiler,
            Publication = report.Publication with
            {
                State = RendererPerfPublicationState.Completed,
                ReturnedByRun = true,
                FinalUnitPublished = true,
                ElapsedMilliseconds = RendererPerfDriver.Value(0, "ms", "unit monotonic clock including publication"),
                EstimatedCostUsd = RendererPerfDriver.Value(0, "USD", "unit elapsed running time estimate")
            }
        };
        return RendererPerfPublicationFiles.Reserve(RendererPerfRawJson.Serialize(normalized, RendererPerfReportJson.MaximumBytes)).Bytes.Length;
    }
    internal static int CompletionBytes(RendererPerfReport report, RendererPerfCompilerInventory inventory)
    {
        // Independently encode four maximally escaped option tables and the actual association cardinality.
        var options = Enumerable.Range(0, 64).Select(i => new GpuDiagnosticOption(
            new string('\u0001', 253) + (char)(i / 64 + 16) + (char)(i / 8 % 8 + 16) + (char)(i % 8 + 16), new string('\u0001', 4096))).ToArray();
        var rows = inventory.Recipes.Select(r => new
        {
            recipeId = r.Id,
            state = "deadline-exceeded",
            reason = new string('\u0001', 256),
            pipelineIndex = int.MaxValue,
            occurrenceIds = inventory.Occurrences.Where(o => o.RecipeId == r.Id).Select(o => o.Id).ToArray(),
            read = new RendererPerfCompilerReadEvidence(new(64, 256, 16384, 256, 16384, 4),
                new(true, uint.MaxValue, ulong.MaxValue, int.MaxValue, int.MaxValue, uint.MaxValue, ulong.MaxValue, false, uint.MaxValue, uint.MaxValue),
                false, new string('\u0001', 256)),
            stage = new RendererPerfCpuSpan("compiler.recipe", null, long.MaxValue, long.MaxValue),
            admission = new RendererPerfCompilerAdmission(r.Id, new(64, 256, 16384, 256, 16384, 4), int.MaxValue, int.MaxValue)
        });
        return RendererPerfCompilerJson.Encode(new
        {
            tables = new[] { options, options, options, options },
            rows = rows.ToArray(),
            originalFailure = report.Publication.OriginalFailure,
            errors = report.Publication.Errors,
            production = report.Compiler.Production,
            finalReferences = report.Sidecars,
            completionSlots = new string(' ', 64),
            header = new string('\u0001', 65536)
        }, CompletionReserve).Length;
    }
    internal static void Verify(RendererPerfReport report, RendererPerfCompilerInventory inventory, long inventoryBytes)
    {
        var account = report.Compiler.Accounting ?? throw new InvalidOperationException("Missing admitted publication account.");
        if (account.BaseRootBytes != BaseBytes(report, inventory) || account.InventoryBytes != inventoryBytes
            || account.CompletionShapeBytes != CompletionBytes(report, inventory) || account.CompletionShapeBytes > CompletionReserve)
            throw new InvalidOperationException("Retained publication base or completion shape disagrees with admission.");
        long committed = 0;
        if (account.Admissions.Length != report.Compiler.Results.Length) throw new InvalidOperationException("Admission/result count mismatch.");
        for (int n = 0; n < account.Admissions.Length; n++)
        {
            var a = account.Admissions[n]; var result = report.Compiler.Results[n];
            long available = checked(RendererPerfReportJson.MaximumBytes - account.BaseRootBytes - inventoryBytes - committed - CompletionReserve);
            var expected = RendererPerfCompilerReadContract.ForAvailable(report.Compiler.Diagnostic!.Backend, available);
            if (expected is null || a.RecipeId != result.RecipeId || a.Request != expected.Request
                || a.MaximumEncodedBytes != expected.MaximumEncodedBytes || result.Read?.Request != a.Request
                || a.CommittedBytes is not { } bytes || bytes < 0 || bytes > a.MaximumEncodedBytes || result.PipelineIndex is not { } index)
                throw new InvalidOperationException("Read request or reservation differs from actual admitted headroom.");
            var read = result.Read!;
            int actual = RendererPerfCompilerReadContract.EncodedResultBytes(new(a.Request, report.Pipelines[index], read.Complete,
                read.Counts!, read.RefusalReason, []));
            if (actual != bytes) throw new InvalidOperationException("Actual report bytes disagree with its commit.");
            committed = checked(committed + bytes);
        }
    }
}
