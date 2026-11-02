using System;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal;

internal readonly record struct PassDepthStencil(bool Depth, bool Stencil)
{
    internal bool Any => Depth || Stencil;
}

internal readonly record struct PassExperimentBlock(int StartCallback, int EndCallbackExclusive,
    long StartTimestamp, long EndTimestamp, bool Discard);

/// <summary>Counters reconcile: OpenedUnknown = DontCareApplied + KeptStoreScopeOpen + ResolvedDuringStop +
/// ResolveFailures + UnresolvedEncoders. DescriptorFailures never opened an Unknown encoder.</summary>
internal sealed record PassExperimentSnapshot(string Name, int BlockCallbacks, PassExperimentBlock[] Blocks,
    long OpenedUnknown, long DontCareApplied, long KeptStoreScopeOpen, long ResolvedDuringStop, long ResolveFailures,
    long DescriptorFailures, long DirectionalEncodersOff, int UnresolvedEncoders)
{
    internal static PassExperimentSnapshot Unavailable() => new(MetalCascadeDiscardExperiment.Name, 72, [], 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>Values of an encoder record's StoreResolution. Null on every encoder outside the directional scope.</summary>
internal static class PassStoreResolution
{
    internal const string DescriptorStore = "descriptorStore";
    internal const string Unresolved = "unresolved";
    internal const string DontCare = "dontCare";
    internal const string StoreScopeOpen = "storeScopeOpen";
    internal const string StoreStopped = "storeStopped";
    internal const string Failed = "failed";
}

/// <summary>
/// Discards the directional cascade pass's working depth and stencil in alternate blocks while sampling. The store
/// action is left Unknown at begin and decided immediately before endEncoding, because only then is it known whether
/// the pass scope ended or a blit split the pass and a later encoder will load this depth. One encoder is tracked at a
/// time, on the callback thread. Metal allows one open encoder per command buffer and the frame list has one.
/// </summary>
internal sealed class MetalCascadeDiscardExperiment(IMetalPassTimingNative native)
{
    internal const string Name = "cascadeDepthDiscard";

    IntPtr _encoder;
    PassDepthStencil _planes;
    bool _scopeEnded;
    long _opened, _dontCare, _keptStore, _stopped, _resolveFailures, _descriptorFailures, _directionalOff;

    internal static bool DiscardBlock(int callback, int blockCallbacks) => callback / blockCallbacks % 2 == 1;

    internal void CountDirectionalOff() => _directionalOff++;

    /// <summary>Marks the descriptor Unknown. A failure restores Store and the encoder opens as an ordinary one.</summary>
    internal bool Prepare(IntPtr descriptor, PassDepthStencil planes)
    {
        try
        {
            native.SetDescriptorStore(descriptor, planes, unknown: true);
            return true;
        }
        catch
        {
            Interlocked.Increment(ref _descriptorFailures);
            Restore(descriptor, planes);
            return false;
        }
    }

    /// <summary>The descriptor goes back to Store on every exit, including a throw from the encoder factory.</summary>
    internal void Restore(IntPtr descriptor, PassDepthStencil planes)
    {
        try { native.SetDescriptorStore(descriptor, planes, unknown: false); }
        catch { Interlocked.Increment(ref _descriptorFailures); }
    }

    internal void Opened(IntPtr encoder, PassDepthStencil planes)
    {
        _encoder = encoder;
        _planes = planes;
        _scopeEnded = false;
        Interlocked.Increment(ref _opened);
    }

    internal void ScopeEnded()
    {
        if (_encoder != IntPtr.Zero) _scopeEnded = true;
    }

    /// <summary>Resolves a tracked encoder and returns how, or null for any other encoder. Store is always safe, so
    /// stopping, disposal and a failed DontCare all fall back to it. Never throws, because endEncoding must follow.</summary>
    internal string? BeforeEndEncoding(IntPtr encoder, bool sampling)
    {
        if (encoder == IntPtr.Zero || encoder != _encoder) return null;
        _encoder = IntPtr.Zero;
        bool discard = sampling && _scopeEnded;
        try
        {
            native.ResolveEncoderStore(encoder, _planes, discard);
        }
        catch
        {
            Interlocked.Increment(ref _resolveFailures);
            try { native.ResolveEncoderStore(encoder, _planes, discard: false); }
            catch { /* The original failure is already counted, and endEncoding still has to run. */ }
            return PassStoreResolution.Failed;
        }
        if (discard) { Interlocked.Increment(ref _dontCare); return PassStoreResolution.DontCare; }
        if (sampling) { Interlocked.Increment(ref _keptStore); return PassStoreResolution.StoreScopeOpen; }
        Interlocked.Increment(ref _stopped);
        return PassStoreResolution.StoreStopped;
    }

    internal PassExperimentSnapshot Snapshot(int blockCallbacks, PassExperimentBlock[] blocks) => new(Name, blockCallbacks, blocks,
        Interlocked.Read(ref _opened), Interlocked.Read(ref _dontCare), Interlocked.Read(ref _keptStore),
        Interlocked.Read(ref _stopped), Interlocked.Read(ref _resolveFailures), Interlocked.Read(ref _descriptorFailures),
        _directionalOff, _encoder != IntPtr.Zero ? 1 : 0);
}
