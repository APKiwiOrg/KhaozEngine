using System;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Lazy hosted GPU fixture for independent garment controls.</summary>
public sealed class SkinnedAttachmentGpuScene : IDisposable
{
    internal const int W = 480, H = 320;
    internal enum Control { Attachments, Skinned, Rigid }
    internal string Backend => throw new NotImplementedException("Task 8 capture fixture is not implemented.");
    internal TemporalFixture Fixture => throw new NotImplementedException("Task 8 capture fixture is not implemented.");

    internal byte[] Capture(Control control, bool gpuSkinning, bool posed = true, float dissolve = 0,
        float complement = 0, bool invert = false, int light = 0, bool shadowOnly = false,
        bool castsShadows = true, bool outlines = false, bool receiverOnly = false) =>
        throw new NotImplementedException("Task 8 capture fixture is not implemented.");

    internal void MotionFrame(bool gpuSkinning, int step) =>
        throw new NotImplementedException("Task 8 capture fixture is not implemented.");

    internal static void Evidence(string name, byte[] pixels) { }
    public void Dispose() { }
}
