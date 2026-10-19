using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageNativeSurface
{
    internal static uint[] Read(IGpuDevice device, Scene3D scene, SkinnedCoverageFrame frame)
    {
        var words = CoverageSelectedReadback.Read(device, scene.CoverageGpuBatchForTests!.Previous);
        var p = frame.Preparation!;
        if (!words.AsSpan((int)p.GpuReservation.CurrentBase, 8).SequenceEqual(p.ResultIdentity.AsSpan(0, 8)))
            throw new InvalidOperationException("Native surface identity does not match the submitted frame.");
        return words;
    }

    internal static string Signature(uint[] words, CoverageGpuReservation r) => CoverageGpuActor.Hash(
        MemoryMarshal.Cast<uint, ModelVertex>(words.AsSpan((int)r.CurrentBase + 16 + r.SourceCount * 16, r.VertexCount * 16)).ToArray());
}
