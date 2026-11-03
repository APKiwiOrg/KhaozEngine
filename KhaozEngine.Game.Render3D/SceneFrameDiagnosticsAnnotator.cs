using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game;

// Reads public last-frame state only. No renderer internals, GPU commands or capture taps.
internal sealed class SceneFrameDiagnosticsAnnotator(Scene3D scene) : IFrameDiagnosticsAnnotator
{
    public string[] Channels { get; } = BuildChannels();

    static string[] BuildChannels()
    {
        var names = new List<string>
        {
            "shadow.Active",
            "shadow.Rendered",
            "shadow.Skipped",
            "shadow.HadPrevious",
            "shadow.AnySkinnedCaster",
            "shadow.SkinnedCastersCleared",
            "shadow.ResolutionChanged",
            "shadow.LightMatrixChanged",
            "shadow.CasterDataChanged",
            "shadow.SkinnedCasterCount",
            "shadow.CascadeCount",
            "shadow.RigidDrawCalls",
            "shadow.SkinnedDrawCalls",
            "shadow.TotalDrawCalls",
            "shadow.PointShadowedLights",
            "shadow.PointStaticRebuilds",
            "shadow.PointDynamicRenders",
            "shadow.PointTransientDemand",
            "shadow.PointTransientRowsRendered",
            "shadow.PointDynamicSkinnedDrawCalls",
            "shadow.PointStaticTransientSkinnedDrawCalls",
            "shadow.PointStaticTransientRigidDrawCalls",
            "shadow.PointStaticFallbackSkinnedDrawCalls",
            "shadow.PointRigidFallbackRowsRendered",
            "shadow.PointRigidTransitionRowsRendered",
            "shadow.PointFaceDrawCalls",
            "shadow.PointSlotsInUse",
            "shadow.TotalRigidSpanCount",
            "stats.DrawCalls",
            "stats.Instances",
            "stats.Triangles",
            "stats.BufferUpdateBytes",
            "stats.InstanceUploadBytes",
            "stats.SkinnedUploadBytes",
            "stats.SkinnedUniformUploadBytes",
            "stats.SpriteUploadBytes",
            "stats.Quads",
            "stats.Flushes",
            "stats.TextureSwitches",
            "stats.UploadBytesPartitioned",
            "scene.DrawnInstances",
            "scene.CulledInstances",
            "scene.DrawnSkinnedInstances",
            "scene.CulledSkinnedInstances",
            "scene.ShadowCasterCandidateCount",
        };
        for (int c = 0; c < ShadowSettings.MaxCascades; c++)
        {
            names.Add($"shadow.RigidSpanCount.{c}");
            names.Add($"scene.ShadowCascadeCasterCount.{c}");
            names.Add($"scene.ShadowCascadeSpanCount.{c}");
        }
        foreach (string matrix in new[] { "view", "projection" })
            for (int r = 1; r <= 4; r++)
                for (int c = 1; c <= 4; c++) names.Add($"camera.{matrix}.M{r}{c}.bits");
        foreach (string vector in new[] { "camera.eye", "camera.forward", "light.direction" })
            foreach (string axis in new[] { "x", "y", "z" }) names.Add(vector + "." + axis + ".bits");
        // GameApp3D follows the F1 overlay's visibility here, and it switches on per-pass CPU Stopwatch reads.
        names.Add("scene.EnableTiming");
        return names.ToArray();
    }

    public void Sample(Span<long> values)
    {
        var shadow = scene.LastShadowPassDiagnostics;
        var stats = scene.LastFrameStats;
        int i = 0;
        values[i++] = shadow.Active ? 1 : 0;
        values[i++] = shadow.Rendered ? 1 : 0;
        values[i++] = shadow.Skipped ? 1 : 0;
        values[i++] = shadow.HadPrevious ? 1 : 0;
        values[i++] = shadow.AnySkinnedCaster ? 1 : 0;
        values[i++] = shadow.SkinnedCastersCleared ? 1 : 0;
        values[i++] = shadow.ResolutionChanged ? 1 : 0;
        values[i++] = shadow.LightMatrixChanged ? 1 : 0;
        values[i++] = shadow.CasterDataChanged ? 1 : 0;
        values[i++] = shadow.SkinnedCasterCount;
        values[i++] = shadow.CascadeCount;
        values[i++] = shadow.RigidDrawCalls;
        values[i++] = shadow.SkinnedDrawCalls;
        values[i++] = shadow.TotalDrawCalls;
        values[i++] = shadow.PointShadowedLights;
        values[i++] = shadow.PointStaticRebuilds;
        values[i++] = shadow.PointDynamicRenders;
        values[i++] = shadow.PointTransientDemand;
        values[i++] = shadow.PointTransientRowsRendered;
        values[i++] = shadow.PointDynamicSkinnedDrawCalls;
        values[i++] = shadow.PointStaticTransientSkinnedDrawCalls;
        values[i++] = shadow.PointStaticTransientRigidDrawCalls;
        values[i++] = shadow.PointStaticFallbackSkinnedDrawCalls;
        values[i++] = shadow.PointRigidFallbackRowsRendered;
        values[i++] = shadow.PointRigidTransitionRowsRendered;
        values[i++] = shadow.PointFaceDrawCalls;
        values[i++] = shadow.PointSlotsInUse;
        values[i++] = shadow.TotalRigidSpanCount;
        values[i++] = stats.DrawCalls;
        values[i++] = stats.Instances;
        values[i++] = stats.Triangles;
        values[i++] = stats.BufferUpdateBytes;
        values[i++] = stats.InstanceUploadBytes;
        values[i++] = stats.SkinnedUploadBytes;
        values[i++] = stats.SkinnedUniformUploadBytes;
        values[i++] = stats.SpriteUploadBytes;
        values[i++] = stats.Quads;
        values[i++] = stats.Flushes;
        values[i++] = stats.TextureSwitches;
        values[i++] = stats.UploadBytesPartitioned;
        values[i++] = scene.DrawnInstances;
        values[i++] = scene.CulledInstances;
        values[i++] = scene.DrawnSkinnedInstances;
        values[i++] = scene.CulledSkinnedInstances;
        values[i++] = scene.ShadowCasterCandidateCount;
        for (int c = 0; c < ShadowSettings.MaxCascades; c++)
        {
            values[i++] = shadow.RigidSpanCount(c);
            values[i++] = scene.ShadowCascadeCasterCount(c);
            values[i++] = scene.ShadowCascadeSpanCount(c);
        }
        IIsoCamera3D camera = scene.CameraOverride ?? scene.Camera;
        Matrix(values, ref i, camera.View);
        Matrix(values, ref i, camera.Projection);
        Vector(values, ref i, camera.Eye);
        Vector(values, ref i, camera.Forward);
        Vector(values, ref i, scene.Post.LightDirection);
        values[i++] = scene.EnableTiming ? 1 : 0;
    }

    static void Matrix(Span<long> values, ref int i, Matrix4x4 m)
    {
        values[i++] = BitConverter.SingleToInt32Bits(m.M11);
        values[i++] = BitConverter.SingleToInt32Bits(m.M12);
        values[i++] = BitConverter.SingleToInt32Bits(m.M13);
        values[i++] = BitConverter.SingleToInt32Bits(m.M14);
        values[i++] = BitConverter.SingleToInt32Bits(m.M21);
        values[i++] = BitConverter.SingleToInt32Bits(m.M22);
        values[i++] = BitConverter.SingleToInt32Bits(m.M23);
        values[i++] = BitConverter.SingleToInt32Bits(m.M24);
        values[i++] = BitConverter.SingleToInt32Bits(m.M31);
        values[i++] = BitConverter.SingleToInt32Bits(m.M32);
        values[i++] = BitConverter.SingleToInt32Bits(m.M33);
        values[i++] = BitConverter.SingleToInt32Bits(m.M34);
        values[i++] = BitConverter.SingleToInt32Bits(m.M41);
        values[i++] = BitConverter.SingleToInt32Bits(m.M42);
        values[i++] = BitConverter.SingleToInt32Bits(m.M43);
        values[i++] = BitConverter.SingleToInt32Bits(m.M44);
    }

    static void Vector(Span<long> values, ref int i, Vector3 v)
    {
        values[i++] = BitConverter.SingleToInt32Bits(v.X);
        values[i++] = BitConverter.SingleToInt32Bits(v.Y);
        values[i++] = BitConverter.SingleToInt32Bits(v.Z);
    }
}
