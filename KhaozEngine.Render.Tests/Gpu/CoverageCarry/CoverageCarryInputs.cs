using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Retained per-actor CPU frame preparation. It never invokes the exact constructor during replay.</summary>
internal sealed class CoverageCarryInputs
{
    readonly CoverageCarryFixture fixture;
    readonly int actor;
    readonly int[] order = new int[4], regions = new int[32];
    readonly CoverageReferenceFrame[] frames = new CoverageReferenceFrame[4];
    readonly CoverageReferenceArithmetic arithmetic = new();
    internal Matrix4x4[] Palette { get; } = new Matrix4x4[2];
    internal CoverageCommandPose[] Commands { get; }
    internal CoverageCarryAnchor Anchor { get; }

    internal CoverageCarryInputs(CoverageCarryFixture fixture, int actor)
    {
        this.fixture = fixture; this.actor = actor;
        CoverageCostGeometry.Pose(0, Palette);
        Commands = fixture.Commands(actor, Palette[0]);
        Anchor = new(fixture.Topology, Palette, Commands);
    }

    internal void Prepare(int frame, float weight = 1, bool rotate = false)
    {
        CoverageCostGeometry.Pose((frame + actor) % 2, Palette);
        Matrix4x4 root = rotate ? Matrix4x4.CreateRotationY(.1f) * Palette[0] : Palette[0];
        for (int i = 0; i < Commands.Length; i++) Commands[i] = Commands[i] with
        { EquipmentWorld = root, CoverageWorld = Commands[i].Support.CoverageLocal * root, TransitionWeight = weight };
        if (!CoverageReferenceFrames.TryPrepare(fixture.Topology, Matrix4x4.Identity, Commands, order, regions, frames,
            arithmetic, out var failure)) throw new InvalidOperationException(failure.ToString());
    }

    internal void Write(Span<uint> frame, uint work, uint surface, uint result, uint anchor, int generation)
    {
        var t = fixture.Topology;
        frame[0] = 0; frame[1] = work; frame[2] = surface; frame[3] = surface; frame[4] = result;
        frame[5] = (uint)t.SourceVertexCount; frame[6] = (uint)t.VertexCount; frame[7] = (uint)t.TriangleCount;
        frame[8] = (uint)t.PositionGroupCount; frame[9] = (uint)t.SourceAliases.Count; frame[10] = (uint)t.ShadingGroupCount;
        frame[11] = 2; frame[12] = (uint)Commands.Length; frame[13] = (uint)generation; frame[14] = anchor;
        for (int b = 0; b < Palette.Length; b++) CoverageGpuUpload.WriteMatrix(Palette[b], frame[(16 + b * 16)..]);
        int start = 48;
        for (int slot = 0; slot < Commands.Length; slot++)
        {
            var reference = frames[slot];
            CoverageGpuUpload.WriteCommand(reference, Commands[reference.Command], 0, frame[(start + slot * 80)..]);
            frame[start + 320 + slot] = (uint)order[slot];
        }
        frame.Slice(start + 324, 32).Fill(uint.MaxValue);
        for (int i = 0; i < t.Map.Regions.Count; i++) frame[start + 324 + i] = unchecked((uint)regions[i]);
    }
}
