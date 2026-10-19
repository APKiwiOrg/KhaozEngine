using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

internal sealed class CoverageFrameArena
{
    internal readonly List<CoveragePreparation> Preparations = new();
    internal readonly List<CoverageEquipmentStage> Equipment = new();
    internal int EquipmentCount;
    internal ulong Generation;
    internal ulong NextPreparation;
    internal bool Closed;
    internal void Begin()
    {
        Generation++;
        Preparations.Clear();
        EquipmentCount = 0;
        Closed = false;
    }
}

internal sealed class CoverageEquipmentStage
{
    internal Scene3D Scene = null!;
    internal ulong Generation;
    internal MotionKey Actor;
    internal ulong Instance;
    internal readonly CoverageEquipmentCommand[] Commands = new CoverageEquipmentCommand[CoverageScratch.MaxCommands];
    internal int CommandCount;
    internal RigidInstanceDraw[] Draws = [];
    internal bool[] Submitted = [], Outlined = [];
    internal int Count;

    internal bool TryCommand(uint id, out CoverageSupportSurface support)
    {
        for (int i = 0; i < CommandCount; i++)
            if (Commands[i].CommandId == id) { support = Commands[i].Support; return true; }
        support = null!;
        return false;
    }
}

internal sealed class CoveragePreparation
{
    internal readonly SkinnedHostConformer Owner;
    internal readonly Matrix4x4[] Palette;
    internal readonly CoverageCommandPose[] Commands = new CoverageCommandPose[CoverageScratch.MaxCommands];
    internal readonly CoverageEquipmentStage[] Equipment = new CoverageEquipmentStage[CoverageScratch.MaxCommands];
    internal readonly SkinnedInstanceDraw?[] Draws, Outlines;
    internal ulong Generation, Id, View;
    internal MotionKey Actor;
    internal Matrix4x4 Model;
    internal int CommandCount;
    internal MeshBounds WorldBounds;
    internal CoverageGpuReservation GpuReservation;
    internal CoverageSurfaceBounds WorldEnvelope;
    internal readonly uint[] ResultIdentity = new uint[8];
    internal PointShadowCasterSphere Sphere;
    internal ulong SphereView;
    internal CoverageFrameOutcome Outcome;
    internal ulong MotionRevision;
    internal bool MotionReusable, MotionPresented, MotionEligible;
    internal CoveragePreparation(SkinnedHostConformer owner, int bones, int materials)
    {
        Owner = owner;
        Palette = new Matrix4x4[bones];
        Draws = new SkinnedInstanceDraw?[materials]; Outlines = new SkinnedInstanceDraw?[materials];
    }
}
