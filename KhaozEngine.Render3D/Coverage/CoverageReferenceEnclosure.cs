using System;
using System.Numerics;
using V = KhaozEngine.Render3D.CoverageVertexEnclosure;
using M = KhaozEngine.Render3D.CoverageEnclosureMath;

namespace KhaozEngine.Render3D;

/// <summary>A separate NC1 evaluation of the reference operation graph, never a constructor or budget decision.</summary>
internal static class CoverageReferenceEnclosure
{
    internal static void Evaluate(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> composedPalette,
        ReadOnlySpan<ModelVertex> referenceFinal, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch referenceScratch, CoverageReferenceEnclosureScratch scratch)
    {
        Validate(topology, composedPalette, referenceFinal, commands, referenceScratch, scratch);
        scratch.Reset(commands.Length);
        if (!CoverageEnclosurePose.Prepare(topology, composedPalette, scratch)
            || !CoverageEnclosurePose.Conform(topology, commands, referenceScratch, scratch)
            || !CoverageEnclosureCorrection.Apply(topology, commands, referenceScratch, scratch)
            || !CoverageEnclosureNormals.Evaluate(topology, commands, referenceScratch, scratch)) return;
        double hostMaximum = 0, equipmentMaximum = 0;
        for (int v = 0; v < topology.VertexCount; v++)
        {
            V final = scratch.MayRefuse ? M.Hull(scratch.After[v], scratch.Before[v]) : scratch.After[v];
            scratch.Model[v] = M.Store(final);
            scratch.World[v] = M.Transform(scratch.Model[v], model);
            double radius = CoverageReferencePrecision.Radius(scratch.World[v], M.ExactCenter(referenceFinal[v].Position, model));
            if (!double.IsFinite(radius)) { scratch.Unbounded(CoverageEnclosureSite.HostWorld, v); return; }
            if (radius > hostMaximum)
            {
                hostMaximum = radius; scratch.WorstVertex = v;
                int group = topology.Vertices[v].PositionGroup;
                scratch.DominantSite = scratch.Representatives[group] < 0 ? CoverageEnclosureSite.HostWorld : scratch.GroupSites[group];
            }
        }
        // Sorted frame slots make both maxima and diagnostics independent of command submission order.
        for (int slot = 0; slot < commands.Length; slot++)
        {
            var command = commands[referenceScratch.Frames[slot].Command];
            var source = command.Support.Source;
            for (int v = 0; v < source.Positions.Length; v++)
            {
                V device = EquipmentWorldVertex(source, v, command.EquipmentWorld);
                double radius = CoverageReferencePrecision.Radius(device, M.ExactCenter(source.Positions[v], command.EquipmentWorld));
                if (!double.IsFinite(radius)) { scratch.Unbounded(CoverageEnclosureSite.EquipmentWorld, v); return; }
                equipmentMaximum = Math.Max(equipmentMaximum, radius);
            }
        }
        if (equipmentMaximum > hostMaximum) scratch.DominantSite = CoverageEnclosureSite.EquipmentWorld;
        scratch.DeviceTolerance = CoverageReferencePrecision.ComposeTolerance(hostMaximum, equipmentMaximum);
    }

    internal static V EquipmentWorldVertex(CoverageSourceTopology source, int vertex, in Matrix4x4 equipmentWorld)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)vertex >= (uint)source.Positions.Length) throw new ArgumentOutOfRangeException(nameof(vertex));
        return M.Transform(M.Point(source.Positions[vertex]), equipmentWorld);
    }

    static void Validate(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<ModelVertex> final,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch reference, CoverageReferenceEnclosureScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(scratch);
        if (!ReferenceEquals(topology, reference.ReferenceTopology.Topology) || !ReferenceEquals(topology, scratch.Rows.Topology))
            throw new ArgumentException("Enclosure workspaces must belong to this topology instance.");
        if (final.Length != topology.VertexCount || commands.Length > scratch.CommandCapacity || commands.Length > reference.CommandCapacity
            || commands.Length != reference.ActiveCommands)
            throw new ArgumentException("Enclosures require exact final lengths and prepared command frames within both capacities.");
        for (int slot = 0; slot < commands.Length; slot++)
            if ((uint)reference.Frames[slot].Command >= (uint)commands.Length)
                throw new ArgumentException("Reference command frames must be prepared before enclosure evaluation.");
        foreach (var vertex in topology.Source.SkinVertices)
            for (int i = 0; i < 4; i++)
                if ((uint)(int)vertex.BoneIndices[i] >= (uint)palette.Length)
                    throw new ArgumentException("Composed palette must contain every source joint read.", nameof(palette));
    }
}
