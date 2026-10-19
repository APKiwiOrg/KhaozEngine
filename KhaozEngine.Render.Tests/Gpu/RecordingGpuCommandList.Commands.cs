using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal sealed partial class RecordingGpuCommandList
{
    internal readonly record struct Command(string Kind, IGpuPipeline? Pipeline, uint Slot, uint Value);
    internal List<Command>? CommandTrace { get; set; }

    void TraceCommand(string kind, IGpuPipeline? pipeline, uint slot = 0, uint value = 0) =>
        CommandTrace?.Add(new Command(kind, pipeline, slot, value));
}
