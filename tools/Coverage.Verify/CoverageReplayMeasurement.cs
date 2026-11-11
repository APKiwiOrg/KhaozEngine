using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Retained scratch for one replay run, shared by acceptance and diagnostic measurement.</summary>
internal sealed class CoverageReplayMeasurement
{
    readonly CoverageRecessionTable table;
    readonly CoverageRefinement topology;
    readonly CoverageBuildInput input;
    readonly CoverageCarryUniforms uniforms;
    readonly CoverageCarryScratch carry;
    readonly CoverageScratch certification;
    readonly ModelVertex[] raw, final;
    internal CoverageReplayMeasurement(CoverageBuildInput input, CoverageRecessionTable table)
    {
        this.input = input; this.table = table;
        topology = input.Topology;
        uniforms = new(topology, input.InverseBind.Length);
        carry = new(topology, input.InverseBind.Length);
        certification = new(topology);
        raw = new ModelVertex[topology.VertexCount];
        final = new ModelVertex[topology.VertexCount];
    }
    internal CoverageReplayPose Measure(CoverageDomain domain, CoverageDenseRow row, int queryBudget,
        Action<CoverageVerificationException>? diagnosticRefusal = null)
    {
        var sample = row.Sample;
        CoverageSampleVerifier.CheckRepresentatives(CoverageSampleVerifier.Representatives(topology, sample), table.Representatives, sample.Id);
        bool selected = table.TrySelect(new(row.ResolvedStateKey), out int entry);
        bool prepared = uniforms.TryPrepare(sample.Palette, input.InverseBind, sample.ActorModel, sample.Commands, out var failure)
            && CoverageCarryReference.TryCarry(topology, uniforms.ComposedPalette, table, -1, uniforms.Commands, carry, raw, out failure);
        // A missing raw pose cannot be measured. Refuse instead of reusing the preceding row's buffers.
        if (!prepared)
            throw new CoverageVerificationException(CoverageVerificationRefusal.SelectedReplayFailure, "Current raw pose could not be prepared.") { Failure = failure }
                .At(domain.Id, row, entry);
        raw.CopyTo(final, 0);
        bool carried = !selected || CoverageCarryReference.TryCarry(topology, uniforms.ComposedPalette, table, entry, uniforms.Commands, carry, final, out failure);
        bool valid = selected && carried
            && CoverageCertification.ValidateGeometry(topology, raw, final, sample.ActorModel, sample.Commands, certification, out failure);
        var classes = new CoverageSkinClasses(topology.Indices, raw, final);
        var changed = Zero; var inherited = Zero; var rawChanged = Zero;
        var equipmentDepths = new List<CoverageEquipmentDepth>();
        foreach (var command in sample.Commands)
        {
            try
            {
                var depth = new CoverageWholeSkinDepth(command.Support);
                var currentChanged = depth.Measure(topology.Indices, final, sample.ActorModel, command.EquipmentWorld, classes.ChangedRecords, queryBudget);
                var currentRaw = depth.Measure(topology.Indices, raw, sample.ActorModel, command.EquipmentWorld, classes.ChangedRecords, queryBudget);
                var currentInherited = depth.Measure(topology.Indices, final, sample.ActorModel, command.EquipmentWorld, classes.Inherited, queryBudget);
                equipmentDepths.Add(new(command.Id, command.EquipmentInstanceId, currentChanged, currentRaw, currentInherited));
                changed = Combine(changed, currentChanged);
                rawChanged = Combine(rawChanged, currentRaw);
                inherited = Combine(inherited, currentInherited);
            }
            catch (CoverageVerificationException error)
            {
                var contextual = error.At(domain.Id, row, entry, command.Id, command.EquipmentInstanceId);
                if (diagnosticRefusal == null) throw contextual;
                diagnosticRefusal(contextual);
            }
        }
        return new(domain.Id, row, entry, valid, failure, classes.ChangedTriangleCount, classes.InheritedTriangleCount,
            Count(classes.PositionsChanged), Count(classes.AttributesOnly), equipmentDepths.AsReadOnly(), changed, rawChanged, inherited);
    }

    static CoverageDepthResult Zero => new(0, 0, 0, true);
    static CoverageDepthResult Combine(CoverageDepthResult a, CoverageDepthResult b) =>
        new(Math.Max(a.ObservedMm, b.ObservedMm), Math.Max(a.UpperEstimateMm, b.UpperEstimateMm), checked(a.Queries + b.Queries), a.Resolved && b.Resolved);
    static int Count(ReadOnlySpan<bool> mask)
    {
        int count = 0;
        foreach (bool value in mask) if (value) count++;
        return count;
    }
}
