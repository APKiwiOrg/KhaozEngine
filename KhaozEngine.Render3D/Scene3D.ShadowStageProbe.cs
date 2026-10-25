using System;
using System.Diagnostics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    enum ShadowStageQueryPurpose { Signature, RowRecording }

    ShadowStageProbeRequest _shadowProbeRequest;
    ShadowStageProbeCounts _shadowProbeCounts;
    ShadowStageProbeTicks _shadowProbeTicks;
    ShadowStageProbeConfiguration _shadowProbeConfiguration;
    bool _shadowProbeActive;
    bool _shadowProbeClockValid;
    Func<long> _shadowProbeTimestamp = Stopwatch.GetTimestamp;
    long _shadowProbeFrequency = Stopwatch.Frequency;

    public void ConfigureShadowStageProbe(in ShadowStageProbeRequest request)
    {
        _shadowProbeRequest = request;
        _shadowProbeActive = request.Epoch != Guid.Empty && request.Sequence > 0
            && request.Mode is ShadowStageProbeMode.Counts or ShadowStageProbeMode.Timings;
        _shadowProbeCounts = default;
        _shadowProbeTicks = new ShadowStageProbeTicks { Frequency = _shadowProbeFrequency };
        _shadowProbeClockValid = true;
        if (_shadowProbeActive) _shadowProbeConfiguration = ShadowProbeConfiguration();
    }

    internal void PublishShadowStageProbe(Frame frame)
    {
        if (!_shadowProbeActive) return;
        try
        {
            var snapshot = new ShadowStageProbeSnapshot(_shadowProbeRequest,
                _shadowProbeClockValid ? ShadowStageProbeStatus.Valid : ShadowStageProbeStatus.InvalidClock,
                _shadowProbeConfiguration, _shadowProbeCounts,
                _shadowProbeRequest.Mode == ShadowStageProbeMode.Timings ? _shadowProbeTicks : null);
            frame.PublishShadowStageSnapshot(in snapshot);
        }
        finally { CancelShadowStageProbe(); }
    }

    public void CancelShadowStageProbe()
    {
        _shadowProbeActive = false;
        _shadowProbeRequest = default;
    }

    internal void SetShadowStageProbeClock(Func<long> reader, long frequency)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _shadowProbeTimestamp = reader;
        _shadowProbeFrequency = frequency;
    }

    long ShadowProbeTimestamp()
    {
        if (!_shadowProbeActive || _shadowProbeRequest.Mode != ShadowStageProbeMode.Timings) return 0;
        _shadowProbeTicks.TimestampReads++;
        return _shadowProbeTimestamp();
    }

    long? ShadowProbeElapsed(long start, long? accumulated)
    {
        if (!_shadowProbeActive || _shadowProbeRequest.Mode != ShadowStageProbeMode.Timings) return accumulated;
        long end = ShadowProbeTimestamp();
        long elapsed = unchecked(end - start);
        if (end < start || elapsed < 0) _shadowProbeClockValid = false;
        return accumulated.GetValueOrDefault() + elapsed;
    }

    long BeginShadowProbePrefix()
    {
        if (_shadowProbeActive) _shadowProbeConfiguration = ShadowProbeConfiguration();
        return ShadowProbeTimestamp();
    }

    ShadowStageProbeConfiguration ShadowProbeConfiguration()
    {
        var shadows = Post.Quality.Shadows;
        var point = shadows.PointShadows;
        return new(EnableTiming, (int)shadows.ResolveFor(_gd.Capabilities).Effective,
            TerrainCastsShadows, ShadowCascadeCulling, UseGpuSkinning, ShadowCullMergeGap,
            _model.ShadowMap.Resolution, _model.ShadowMap.CascadeCount, point.Enabled,
            PointShadowFaceResolution, PointShadowRows, PointShadowTransientRows,
            Math.Max(0, point.MaxStaticRebuildsPerFrame), Math.Max(0, point.MaxDynamicLightsPerFrame), (int)point.Filter);
    }

    static ShadowStageProbeQueryCounts AddShadowProbeQuery(in ShadowStageProbeQueryCounts previous,
        int tests, int accepted, int sortInputs) => new(previous.Calls + 1,
            previous.ExactTests + tests, previous.Accepted + accepted, previous.SortInputs + sortInputs);

    void RecordShadowProbeRigidQuery(ShadowStageQueryPurpose purpose, int beforeTests, int beforeSort, long start)
    {
        if (!_shadowProbeActive) return;
        long? elapsed = ShadowProbeElapsed(start, purpose == ShadowStageQueryPurpose.Signature
            ? _shadowProbeTicks.RigidSignatureQuery : _shadowProbeTicks.RigidRowQuery);
        int tests = _pointCasterIndex.TouchTests - beforeTests;
        int sorts = _pointCasterIndex.SortInputs - beforeSort;
        if (purpose == ShadowStageQueryPurpose.Signature)
        {
            _shadowProbeCounts.RigidSignatureQueries = AddShadowProbeQuery(_shadowProbeCounts.RigidSignatureQueries, tests, _pointCasterHits.Count, sorts);
            _shadowProbeTicks.RigidSignatureQuery = elapsed;
        }
        else
        {
            _shadowProbeCounts.RigidRowQueries = AddShadowProbeQuery(_shadowProbeCounts.RigidRowQueries, tests, _pointCasterHits.Count, sorts);
            _shadowProbeTicks.RigidRowQuery = elapsed;
        }
    }

    void RecordShadowProbeFoliageQuery(ShadowStageQueryPurpose purpose, int beforeTests, int beforeSort, long start)
    {
        if (!_shadowProbeActive) return;
        long? elapsed = ShadowProbeElapsed(start, purpose == ShadowStageQueryPurpose.Signature
            ? _shadowProbeTicks.FoliageSignatureQuery : _shadowProbeTicks.FoliageRowQuery);
        int tests = _foliagePointIndex.TouchTests - beforeTests;
        int sorts = _foliagePointIndex.SortInputs - beforeSort;
        if (purpose == ShadowStageQueryPurpose.Signature)
        {
            _shadowProbeCounts.FoliageSignatureQueries = AddShadowProbeQuery(_shadowProbeCounts.FoliageSignatureQueries, tests, _foliagePointHits.Count, sorts);
            _shadowProbeTicks.FoliageSignatureQuery = elapsed;
        }
        else
        {
            _shadowProbeCounts.FoliageRowQueries = AddShadowProbeQuery(_shadowProbeCounts.FoliageRowQueries, tests, _foliagePointHits.Count, sorts);
            _shadowProbeTicks.FoliageRowQuery = elapsed;
        }
    }

    void RecordShadowProbeFoliageBuild(ShadowStageFoliageBuildOrigin origin, long start)
    {
        if (!_shadowProbeActive) return;
        _shadowProbeTicks.FoliageIndexBuild = ShadowProbeElapsed(start, _shadowProbeTicks.FoliageIndexBuild);
        _shadowProbeCounts.FoliageIndexBuilt = true;
        _shadowProbeCounts.FoliageBuildOrigin = origin;
        _shadowProbeCounts.FoliageSources = _foliagePointSources.Count;
        _shadowProbeCounts.FoliageRanges = _foliagePointRanges.Count;
        _shadowProbeCounts.FoliageSelectedProxies = _foliagePointIndex.Count;
    }
}
