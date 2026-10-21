using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Scene capability and cold shared uploads. Replacements publish atomically and retire as one batch.</summary>
internal sealed class CoverageGpuResources : IDisposable
{
    readonly IGpuDevice device;
    readonly GpuRetireQueue retired;
    readonly Dictionary<(CoverageSourceTopology, HostCoverageMap, int), CoverageGpuHost> hosts = new();
    readonly List<CoverageGpuActor> actors = new();
    readonly Dictionary<CoverageSupportSurface, CoverageGpuSupportUpload> supportUploads = new();
    readonly CoverageGpuPipeline? pipeline;
    bool dirty;

    internal CoverageGpuResources(IGpuDevice device, GpuRetireQueue retired)
    {
        this.device = device; this.retired = retired;
        Exception? failure = null;
        if (device.Capabilities.SupportsCompute)
        {
            try { pipeline = new(device); }
            catch (Exception error) { failure = error; }
        }
        if (pipeline is null) Log.For<CoverageGpuResources>().Warn(
            "Equipment coverage capability failure (UnsupportedAdapter). Using vertex skinning.", failure);
    }

    internal bool Available => pipeline is not null;
    internal CoverageGpuBatch? Batch { get; private set; }

    internal CoverageGpuActor Acquire(CoverageSourceTopology source, HostCoverageMap map, int bones)
    {
        var key = (source, map, bones);
        if (!hosts.TryGetValue(key, out var host))
        {
            var topology = CoverageRefinement.Build(source, map);
            host = new(topology, CoverageGpuUpload.Host(topology, bones), new(source));
            hosts.Add(key, host);
        }
        var actor = new CoverageGpuActor(host);
        actors.Add(actor);
        try { Rebuild(); return actor; }
        catch { actors.Remove(actor); PruneHosts(); throw; }
    }

    internal void ReserveSupports(CoverageGpuActor actor, ReadOnlySpan<CoverageSupportSurface> supports)
    {
        if (!actor.Live) throw new InvalidOperationException("Coverage host was unloaded.");
        if (supports.Length > CoverageScratch.MaxCommands) throw new ArgumentOutOfRangeException(nameof(supports));
        foreach (var support in supports) ArgumentNullException.ThrowIfNull(support);
        if (actor.HasSupports(supports)) return;
        var replacement = supports.ToArray();
        Rebuild(actor, replacement);
        actor.Supports = replacement;
    }

    internal void Release(CoverageGpuActor actor)
    {
        if (!actor.Live) return;
        actor.Live = false;
        actor.ReleaseHistory();
        actors.Remove(actor);
        PruneHosts();
        dirty = true;
        if (actors.Count == 0)
        {
            retired.Retire(Batch); Batch = null; dirty = false; supportUploads.Clear();
        }
    }

    void PruneHosts()
    {
        foreach (var pair in hosts.ToArray())
            if (!actors.Any(a => ReferenceEquals(a.Host, pair.Value))) hosts.Remove(pair.Key);
    }

    internal void PrepareFrame()
    {
        if (dirty) Rebuild();
        Batch?.BeginFrame();
    }

    // Everything created here is provisional until all buffers, uploads and bindings succeed.
    void Rebuild(CoverageGpuActor? replacing = null, CoverageSupportSurface[]? replacement = null)
    {
        if (!Available) return;
        var hostOffsets = new Dictionary<CoverageGpuHost, uint>();
        var supports = new Dictionary<CoverageSupportSurface, (CoverageGpuSupportUpload Upload, uint Offset)>();
        int[] supportCapacities = actors.Select(actor => (ReferenceEquals(actor, replacing) ? replacement! : actor.Supports)
            .Select(s => s.InwardTriangles.Count).DefaultIfEmpty(0).Max()).ToArray();
        uint staticWords = 0, frames = 0, work = 0, surfaces = 0;
        for (int i = 0; i < actors.Count; i++)
        {
            var actor = actors[i];
            if (hostOffsets.TryAdd(actor.Host, staticWords)) staticWords = Add(staticWords, (uint)actor.Host.Upload.Words.Length);
            frames = Add(frames, CoverageGpuLayout.FrameWords(actor.Host.Upload.BoneCount));
            work = Add(work, CoverageGpuLayout.WorkWords(actor.Host.Upload, supportCapacities[i]));
            surfaces = Add(surfaces, CoverageGpuLayout.SurfaceWords(actor.Host.Upload));
        }
        foreach (var actor in actors)
            foreach (var support in ReferenceEquals(actor, replacing) ? replacement! : actor.Supports)
                if (!supports.ContainsKey(support))
                {
                    var upload = supportUploads.TryGetValue(support, out var existing) ? existing : CoverageGpuUpload.Support(support);
                    supports.Add(support, (upload, staticWords)); staticWords = Add(staticWords, (uint)upload.Words.Length);
                }
        var words = new uint[staticWords];
        foreach (var pair in hostOffsets) pair.Key.Upload.Words.CopyTo(words, (int)pair.Value);
        foreach (var pair in supports) pair.Value.Upload.Words.CopyTo(words, (int)pair.Value.Offset);
        var reservations = new CoverageGpuReservation[actors.Count];
        var supportOffsets = new uint[actors.Count][];
        var leases = new CoverageGpuHistoryLease[actors.Count];
        var next = new CoverageGpuBatch(device);
        try
        {
            next.Reserve(actors.Count, checked((int)frames), checked((int)work), checked((int)surfaces));
            next.UploadStatic(words);
            uint frameBase = (uint)actors.Count, workBase = 0, surfaceBase = 0;
            for (int i = 0; i < actors.Count; i++)
            {
                var actor = actors[i]; var h = actor.Host.Upload;
                leases[i] = new(next, retired);
                reservations[i] = CoverageGpuLayout.CreateReservation(i, hostOffsets[actor.Host], frameBase,
                    workBase, surfaceBase, surfaceBase, h, supportCapacities[i]);
                var requested = ReferenceEquals(actor, replacing) ? replacement! : actor.Supports;
                supportOffsets[i] = requested.Select(s => supports[s].Offset).ToArray();
                device.UpdateBuffer(next.Work, CoverageGpuLayout.Bytes(workBase), CoverageGpuLayout.WorkHeader(h, supportCapacities[i]));
                frameBase = Add(frameBase, CoverageGpuLayout.FrameWords(h.BoneCount));
                workBase = Add(workBase, CoverageGpuLayout.WorkWords(h, supportCapacities[i]));
                surfaceBase = Add(surfaceBase, CoverageGpuLayout.SurfaceWords(h));
            }
            pipeline!.Prepare(next);
        }
        catch { next.Dispose(); throw; }
        for (int i = 0; i < actors.Count; i++)
        {
            actors[i].Reservation = reservations[i]; actors[i].SupportOffsets = supportOffsets[i];
            actors[i].FrameHistoryLease = leases[i];
        }
        supportUploads.Clear();
        foreach (var pair in supports) supportUploads.Add(pair.Key, pair.Value.Upload);
        retired.Retire(Batch); Batch = next; dirty = false;
    }

    static uint Add(uint a, uint b) => CoverageGpuLayout.Words((long)a + b);
    internal void Record(IGpuCommandList commands) => pipeline!.Record(commands, Batch!);
    public void Dispose() { foreach (var actor in actors) actor.ReleaseHistory(); Batch?.Dispose(); pipeline?.Dispose(); actors.Clear(); hosts.Clear(); supportUploads.Clear(); }
}

internal sealed record CoverageGpuHost(CoverageRefinement Topology, CoverageGpuHostUpload Upload, CoverageActorBounds Bounds);

/// <summary>One actor's cold reservation. Frame words contain inputs only, never CPU-constructed vertices.</summary>
internal sealed partial class CoverageGpuActor(CoverageGpuHost host)
{
    internal readonly CoverageGpuHost Host = host;
    internal readonly uint[] Frame = new uint[CoverageGpuLayout.FrameWords(host.Upload.BoneCount)];
    internal CoverageSupportSurface[] Supports = [];
    internal uint[] SupportOffsets = [];
    internal CoverageGpuReservation Reservation;
    internal bool Live = true;
    internal bool HasSupports(ReadOnlySpan<CoverageSupportSurface> supports)
    {
        foreach (var support in supports) if (Array.IndexOf(Supports, support) < 0) return false;
        foreach (var support in Supports) if (!supports.Contains(support)) return false;
        return true;
    }
    internal bool TrySupport(CoverageSupportSurface support, out uint offset)
    {
        for (int i = 0; i < Supports.Length; i++)
            if (ReferenceEquals(Supports[i], support)) { offset = SupportOffsets.Length > i ? SupportOffsets[i] : 0; return true; }
        offset = 0; return false;
    }
}
