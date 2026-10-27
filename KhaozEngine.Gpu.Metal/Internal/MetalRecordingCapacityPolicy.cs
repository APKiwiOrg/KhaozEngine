using System;

namespace KhaozEngine.Gpu.Metal.Internal;

internal readonly record struct MetalRecordingCapacityPolicy(bool Enabled, int BaseFrames, int RecordingSlots,
    string? UnrecognizedBaseValue)
{
    internal const string EnvVarName = "KE_METAL_RECORDING_SLOTS";
    internal int UncommittedBound => MetalFramesInFlight.UncommittedBufferBound(RecordingSlots);

    internal static MetalRecordingCapacityPolicy Resolve(string? baseValue, string? recordingSlots)
    {
        int frames = MetalFramesInFlight.Resolve(baseValue, out string? unrecognized);
        if (recordingSlots is null) return new(false, frames, frames, unrecognized);
        if (recordingSlots is not ("3" or "6"))
            throw new ArgumentException(EnvVarName + " must be exactly 3 or 6.", nameof(recordingSlots));
        if (frames != 3 || unrecognized is not null)
            throw new ArgumentException(EnvVarName + " requires a recognized base count of 3.", nameof(baseValue));
        return new(true, frames, recordingSlots == "3" ? 3 : 6, null);
    }

    internal void ValidateActual(int slots, int? drawables, int retirementCap)
    {
        if (slots != RecordingSlots || (drawables.HasValue && drawables != 3) || retirementCap != 8)
            throw new InvalidOperationException("Metal recording-capacity startup readback disagrees with the private policy.");
    }

    // A failure during cleanup must not replace the original configuration or readback error.
    internal static void Initialize(Action configure, Action cleanup)
    {
        try { configure(); }
        catch
        {
            try { cleanup(); }
            catch (Exception) { }
            throw;
        }
    }
}
