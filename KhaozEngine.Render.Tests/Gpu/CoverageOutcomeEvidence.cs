using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal readonly record struct CoverageOutcomeExpected(ulong FrameId, ulong ViewId, MotionKey Actor, string Host)
{
    internal static CoverageOutcomeExpected Capture(CoveragePullingTestScene.Actor actor)
    {
        var preparation = actor.Frame.Preparation!;
        return new(preparation.Generation, preparation.View, preparation.Actor, actor.Topology.HostIdentity);
    }

    internal void AssertMatches(CoverageGpuOutcome actual)
    {
        Assert.Equal(FrameId, actual.FrameId);
        Assert.Equal(ViewId, actual.ViewId);
        Assert.Equal(Actor, actual.Actor);
        if (!actual.Outcome.Failure.IsSuccess) Assert.Equal(Host, actual.Outcome.Failure.HostIdentity);
    }

    internal CoverageOutcomeObservation Observe(string scene, CoverageGpuOutcome actual) => new(scene, Host, actual);
}

internal readonly record struct CoverageOutcomeObservation(string Scene, string CapturedHostIdentity, CoverageGpuOutcome Value);

/// <summary>Bounded decoded native outcome evidence. Captured host metadata is labeled separately from API results.</summary>
internal static class CoverageOutcomeEvidence
{
    internal static string Write(IGpuDevice device, string testCase, object facts, params CoverageOutcomeObservation[] rows)
    {
        string directory = Path.Combine(CoverageArtifacts.DirectoryPath
            ?? "/tmp/grimhollow-equipment-coverage-task13/fix1", "outcomes");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, testCase + "." + device.Backend + ".json");
        var packet = new
        {
            EvidenceKind = "decoded-native-gpu-outcomes",
            ResultSource = "Scene3D.TryReadCoverageOutcome",
            Backend = device.Backend.ToString(),
            Device = device.Capabilities.DeviceName,
            Case = testCase,
            Facts = facts,
            Outcomes = rows.Select(row => new
            {
                row.Scene,
                row.CapturedHostIdentity,
                row.Value.FrameId,
                row.Value.ViewId,
                Actor = row.Value.Actor.Value,
                Presentation = row.Value.Outcome.Presentation.ToString(),
                FailureCode = row.Value.Outcome.Failure.Code.ToString(),
                FailureCodeValue = (int)row.Value.Outcome.Failure.Code,
                ReportedHostIdentity = row.Value.Outcome.Failure.HostIdentity,
                row.Value.Outcome.Failure.EquipmentInstanceId,
                row.Value.Outcome.Failure.CommandId,
                row.Value.Outcome.Failure.Count,
                row.Value.Outcome.Failure.Limit,
            }).ToArray()
        };
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(packet, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
        return path;
    }
}
