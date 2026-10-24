using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Cold-bound pass lookup. The recorder retains marker order, nesting and exclusive command ownership.</summary>
internal sealed class RendererPerfPassObserver : IRenderPassObserver
{
    readonly RendererPerfRecorder _recorder;
    readonly Dictionary<RenderPassKey, int> _indices;
    readonly RendererPerfPassDefinition[] _definitions;
    readonly int[] _pointRows;
    readonly IGpuCommandList?[] _skippedPointRows;
    readonly bool[] _fusedMotion;
    readonly bool _detailed;

    internal RendererPerfPassObserver(RendererPerfRecorder recorder, RendererPerfLayout layout,
        ReadOnlySpan<RendererPerfPassDefinition> definitions)
    {
        _recorder = recorder;
        _detailed = layout == RendererPerfLayout.PerPass;
        _indices = new(definitions.Length);
        _definitions = definitions.ToArray();
        _skippedPointRows = new IGpuCommandList?[definitions.Length];
        _fusedMotion = new bool[definitions.Length];
        var pointRows = new List<int>();
        for (int i = 0; i < definitions.Length; i++)
        {
            _indices.Add(new(definitions[i].Id, definitions[i].Occurrence), i);
            if (definitions[i].Id is "shadow.point.static" or "shadow.point.dynamic") pointRows.Add(i);
        }
        pointRows.Sort((a, b) => _definitions[a].BeginMarker.CompareTo(_definitions[b].BeginMarker));
        _pointRows = pointRows.ToArray();
    }

    /// <summary>Reset cached-row notifications and fused flags before each recorded frame.</summary>
    internal void BeginFrame()
    {
        Array.Clear(_fusedMotion);
        Array.Clear(_skippedPointRows);
    }
    internal ReadOnlySpan<bool> FusedMotionPasses => _fusedMotion;
    int Index(RenderPassKey key)
    {
        if (_indices.TryGetValue(key, out int index)) return index;
        _recorder.Fail("An observer pass is outside the frozen layout.");
        return -1;
    }
    public void BeginPass(IGpuCommandList commands, RenderPassKey key)
    {
        if (_detailed && Index(key) is >= 0 and var index)
        {
            FlushSkippedPointRows(_definitions[index].BeginMarker);
            _recorder.BeginPass(commands, index);
        }
    }
    public void EndPass(IGpuCommandList commands, RenderPassKey key)
    {
        if (_detailed && Index(key) is >= 0 and var index)
        {
            FlushSkippedPointRows(_definitions[index].EndMarker);
            _recorder.EndPass(commands, index);
        }
    }
    public void SkipPass(IGpuCommandList commands, RenderPassKey key)
    {
        if (_detailed && Index(key) is >= 0 and var index)
        {
            if (key.Id is "shadow.point.static" or "shadow.point.dynamic")
            {
                if (_skippedPointRows[index] is not null) _recorder.Fail("A cached point row was reported twice.");
                _skippedPointRows[index] = commands;
                return;
            }
            FlushSkippedPointRows(_definitions[index].BeginMarker);
            _recorder.SkipPass(commands, index);
        }
    }
    // Only cached rows may move. Executed callbacks retain native order and recorder validation.
    void FlushSkippedPointRows(uint before)
    {
        foreach (int index in _pointRows)
            if (_skippedPointRows[index] is { } commands && _definitions[index].BeginMarker < before)
            {
                _skippedPointRows[index] = null;
                _recorder.SkipPass(commands, index);
            }
    }
    public void FusedMotion(IGpuCommandList commands, RenderPassKey key)
    {
        if (_detailed && Index(key) is >= 0 and var index) _fusedMotion[index] = true;
    }
    public void BeginCpuPhase(string id) => _recorder.BeginCpuPhase(id);
    public void EndCpuPhase(string id) => _recorder.EndCpuPhase(id);
}
