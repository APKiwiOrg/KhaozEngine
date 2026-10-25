using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render2D;

public sealed partial class SpriteBatch
{
    bool _disposalCompleted;
    internal bool DisposalSafeForDependants { get; private set; }

    public void Dispose()
    {
        if (_disposalCompleted) return;
        DisposalSafeForDependants = false;
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        int ordinal = 0;
        bool Release(string kind, Action release)
        {
            int index = ordinal++;
            try { release(); return true; }
            catch (Exception error)
            {
                if (error is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
                else failures.Add(new("renderer.session.dispose.ui." + kind + "." + index, error));
                return false;
            }
        }
        // An empty retire queue does not drain. Live batch resources still require completed GPU work.
        if (!Release("drain", () =>
        {
            if (GpuRecording.OpenOwner(_gd) is { } owner) throw new GpuDrainDuringRecordingException(owner);
            if (_retire.PendingCount == 0) _gd.WaitForIdle();
        }) || !Release("retirement", _retire.Dispose))
            throw new GpuDiagnosticFailureException(failures.ToImmutable());
        DisposalSafeForDependants = true;
        _disposalCompleted = true;
        foreach (List<IGpuBuffer> buffers in _vbRing)
            foreach (var buffer in buffers) Release("vertex", () => buffer?.Dispose());
        foreach (var set in _sets.Values) Release("set", set.Dispose);
        if (_maskedResources is { } masked) Release("masked", masked.Dispose);
        Release("viewSet", _vpSet.Dispose);
        Release("viewUniform", _vpUbo.Dispose);
        Release("viewLayout", _vpLayout.Dispose);
        Release("pipeline", _pipeline.Dispose);
        Release("additivePipeline", _additivePipeline.Dispose);
        Release("layout", _layout.Dispose);
        Release("shaders", _shaders.Dispose);
        if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }
}
