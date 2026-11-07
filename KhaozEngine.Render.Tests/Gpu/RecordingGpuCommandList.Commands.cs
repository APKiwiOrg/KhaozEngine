using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal sealed partial class RecordingGpuCommandList
{
    internal readonly record struct Command(string Kind, IGpuPipeline? Pipeline, uint Slot, uint Value);
    internal List<Command>? CommandTrace { get; set; }

    /// <summary>Every framebuffer bound since the last <see cref="Clear"/>, in order, so a test can read a frame's
    /// target order by what each one writes. <see cref="Upload.FramebufferBindsBefore"/> indexes it.</summary>
    internal List<IGpuFramebuffer> FramebufferBindings { get; } = [];

    readonly List<IGpuFramebuffer?> _colourClearTargets = [];

    /// <summary>The framebuffer bound for each entry of <see cref="ColourClears"/>, in the same order.</summary>
    internal IReadOnlyList<IGpuFramebuffer?> ColourClearTargets => _colourClearTargets;

    void NoteFramebuffer(IGpuFramebuffer framebuffer)
    {
        _framebufferBinds++;
        FramebufferBindings.Add(framebuffer);
    }

    void TraceCommand(string kind, IGpuPipeline? pipeline, uint slot = 0, uint value = 0) =>
        CommandTrace?.Add(new Command(kind, pipeline, slot, value));
}
