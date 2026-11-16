using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

// The existing seams are thread-local. A stable token makes hit contracts portable to hosts
// without a native controls reader. Native-control changes have their own kernel contracts.
internal sealed class ContactQueryCacheScope : IDisposable
{
    readonly bool waterBypass = TileMediumSampler.BypassTraceMemoForTests;
    readonly bool proposalBypass = SupportProposalAdmission.BypassForTests;
    readonly TileMediumSampler.ControlRead? waterReader = TileMediumSampler.ControlReaderForTests;
    readonly SupportProposalAdmission.ControlRead? proposalReader = SupportProposalAdmission.ControlReaderForTests;
    readonly bool waterWork = TileWaterQueryWork.Enabled;
    readonly bool bufferWork = MovementWaterBufferWork.Enabled;
    readonly int rents = MovementWaterBufferWork.Rents, clears = MovementWaterBufferWork.ClearedReferences;

    internal ContactQueryCacheScope()
    {
        TileMediumSampler.ControlReaderForTests = Read;
        SupportProposalAdmission.ControlReaderForTests = Read;
        SupportProposalAdmission.ResetForTests();
        SetCached(true);
    }

    internal static void SetCached(bool cached)
    {
        TileMediumSampler.BypassTraceMemoForTests = !cached;
        SupportProposalAdmission.BypassForTests = !cached;
        TileWaterQueryWork.Reset();
        // Cleanup is not a cache. The diagnostic lane performs the original nondefault count
        // scan before bulk clearing complete records, while the optimized lane skips that scan.
        MovementWaterBufferWork.Enabled = !cached;
        MovementWaterBufferWork.Rents = MovementWaterBufferWork.ClearedReferences = 0;
    }

    static bool Read(out ulong controls) { controls = 0x5345; return true; }

    public void Dispose()
    {
        TileMediumSampler.BypassTraceMemoForTests = waterBypass;
        SupportProposalAdmission.BypassForTests = proposalBypass;
        TileMediumSampler.ControlReaderForTests = waterReader;
        SupportProposalAdmission.ControlReaderForTests = proposalReader;
        SupportProposalAdmission.ResetForTests();
        TileWaterQueryWork.Enabled = waterWork;
        MovementWaterBufferWork.Enabled = bufferWork;
        MovementWaterBufferWork.Rents = rents;
        MovementWaterBufferWork.ClearedReferences = clears;
    }
}
