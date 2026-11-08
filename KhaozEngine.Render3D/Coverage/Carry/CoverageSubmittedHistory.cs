using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Actor-local staged and submitted metadata. The scene aggregate is the sole submission participant.</summary>
internal sealed class CoverageSubmittedHistory
{
    readonly CoverageCommandPose[] configuration = new CoverageCommandPose[CoverageCarryUniforms.MaxCommands];
    object[] tokens = [];
    int commandCount;
    internal CoverageHistorySnapshot Committed { get; private set; }
    internal object? View { get; private set; }
    internal bool HasSurface { get; private set; }
    internal long Serial { get; private set; }

    internal void Reserve(int slots)
    {
        if (tokens.Length >= slots) return;
        tokens = new object[slots];
        for (int i = 0; i < slots; i++) tokens[i] = new object();
    }
    internal object Token(int slot) => tokens[slot];
    internal bool Corresponds(CoveragePreparation p, object view, long serial)
    {
        if (!HasSurface || Serial != serial || !ReferenceEquals(View, view) || commandCount != p.CommandCount) return false;
        for (int c = 0; c < commandCount; c++)
        {
            var now = p.Commands[c]; bool found = false;
            for (int old = 0; old < commandCount; old++)
            {
                var last = configuration[old];
                found |= now.Id == last.Id && now.EquipmentInstanceId == last.EquipmentInstanceId
                    && now.RegionIndex == last.RegionIndex && ReferenceEquals(now.Support, last.Support)
                    && ReferenceEquals(now.Topology, last.Topology);
            }
            if (!found) return false;
        }
        return true;
    }
    internal void Commit(CoveragePreparation p, object view, long serial)
    {
        commandCount = p.CommandCount;
        p.Commands.AsSpan(0, commandCount).CopyTo(configuration);
        Committed = new(p.Owner.Actor, p.Raw ? -1 : p.EntryIndex, p.OutputSlot,
            tokens[p.OutputSlot], p.Model, p.PublicationRevision, false);
        View = view; Serial = serial; HasSurface = true;
    }

    internal void Discontinue(MotionKey actor, long serial)
    {
        HasSurface = false; Serial = serial; View = null;
        Committed = new(actor, -1, -1, null, default, 0, true);
    }
}

/// <summary>Immutable observations. SurfaceToken is a cold identity object, never a GPU resource owner.</summary>
internal readonly record struct CoverageHistorySnapshot(MotionKey Actor, int Entry, int Slot,
    object? SurfaceToken, Matrix4x4 Model, ulong Revision, bool Discontinuity);
