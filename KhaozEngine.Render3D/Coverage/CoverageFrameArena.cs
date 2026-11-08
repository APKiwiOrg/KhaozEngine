using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D;

internal sealed class CoverageFrameArena
{
    internal readonly List<CoveragePreparation> Preparations = [];
    internal ulong Generation;
    internal void Begin() { Generation++; Preparations.Clear(); }
}

internal sealed class CoveragePreparation
{
    internal readonly SkinnedHostCoverage Owner;
    internal readonly Matrix4x4[] Palette;
    internal readonly CoverageCommandPose[] Commands = new CoverageCommandPose[CoverageCarryUniforms.MaxCommands];
    internal readonly CoverageEquipmentStage?[] Equipment = new CoverageEquipmentStage?[CoverageCarryUniforms.MaxCommands];
    internal readonly SkinnedInstanceDraw?[] Draws, Outlines;
    internal ulong Generation, ListedGeneration, Revision;
    internal Matrix4x4 Model;
    internal CoverageStateKey StateKey;
    internal int EntryIndex, CommandCount;
    internal CoveragePreparation(SkinnedHostCoverage owner, int bones, int materials)
    {
        Owner = owner;
        Palette = new Matrix4x4[bones];
        Draws = new SkinnedInstanceDraw?[materials]; Outlines = new SkinnedInstanceDraw?[materials];
    }
}

internal sealed class CoverageEquipmentStage(SkinnedHostCoverage owner)
{
    internal readonly SkinnedHostCoverage Owner = owner;
    internal ulong Generation, Revision;
    internal ulong Instance;
    internal readonly CoverageEquipmentCommand[] Commands = new CoverageEquipmentCommand[CoverageCarryUniforms.MaxCommands];
    internal int CommandCount, Count;
    internal RigidInstanceDraw[] Draws = [];
    internal bool[] Submitted = [], Outlined = [];

    internal void Reserve(int count)
    {
        if (Draws.Length >= count) return;
        Draws = new RigidInstanceDraw[count]; Submitted = new bool[count]; Outlined = new bool[count];
    }
    internal bool TryCommand(uint id, out CoverageSupportSurface support)
    {
        for (int i = 0; i < CommandCount; i++)
            if (Commands[i].CommandId == id) { support = Commands[i].Support; return true; }
        support = null!;
        return false;
    }
}
