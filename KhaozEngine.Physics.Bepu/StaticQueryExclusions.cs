using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepuPhysics.Collidables;
using SeamStaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Owned seam-handle selection resolved against the source's live backend mapping.</summary>
internal sealed class StaticQueryExclusions
{
    private readonly HashSet<SeamStaticHandle> _excludedStatics;
    private readonly IReadOnlyDictionary<int, int> _reverseHandles;
    internal int SelectedGeneralSweepStatics;
    internal CapsuleSweepPruningBounds GeneralSweepBounds;

    internal bool ExcludesSweepStatic(int id) => _excludedStatics.Contains(new SeamStaticHandle(id));

    internal void InitializeGeneralSweepStatics(HashSet<int> general)
    {
        SelectedGeneralSweepStatics = general.Count;
        foreach (SeamStaticHandle handle in _excludedStatics)
            if (general.Contains(handle.Value)) SelectedGeneralSweepStatics--;
    }

    public StaticQueryExclusions(SeamStaticHandle[] snapshot, IReadOnlyDictionary<int, int> reverseHandles)
    {
        _excludedStatics = new HashSet<SeamStaticHandle>(snapshot);
        _reverseHandles = reverseHandles;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Allows(CollidableReference collidable)
        => collidable.Mobility != CollidableMobility.Static
            || !_reverseHandles.TryGetValue(collidable.StaticHandle.Value, out int seamId)
            || !_excludedStatics.Contains(new SeamStaticHandle(seamId));
}
