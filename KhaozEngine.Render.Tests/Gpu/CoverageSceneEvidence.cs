using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Test-owned selected copy in the frame recording, consumed after its one submission and assertion drain.</summary>
internal sealed class CoverageSceneEvidence(TemporalFixture fixture) : IDisposable
{
    readonly List<(string Pass, CoveragePulledDraw Draw)> pulls = [];
    IGpuBuffer? staging, submittedCurrent, submittedPrevious;
    CoverageSceneSurfaceEvidence[] surfaces = [];
    internal CoverageSceneSurfaceEvidence[] Surfaces { get; private set; } = [];

    internal byte[] Frame(Action<Scene3D, int> draw, Func<SkinnedCoverageFrame[]> frames)
    {
        Assert.Null(fixture.RecordFrameEvidenceForTests);
        Assert.Null(fixture.CompleteFrameEvidenceForTests);
        pulls.Clear();
        fixture.Scene.CoveragePullObserverForTests = (pass, consumer) =>
        {
            pulls.Add((pass, consumer));
        };
        fixture.RecordFrameEvidenceForTests = commands => Record(commands, frames());
        fixture.CompleteFrameEvidenceForTests = Complete;
        try
        {
            int submissions = fixture.FrameSubmissionsForTests, drains = fixture.AssertionDrainsForTests;
            byte[] colour = fixture.Frame(draw);
            Assert.Equal(1, fixture.FrameSubmissionsForTests - submissions);
            Assert.Equal(1, fixture.AssertionDrainsForTests - drains);
            return colour;
        }
        finally
        {
            fixture.RecordFrameEvidenceForTests = null;
            fixture.CompleteFrameEvidenceForTests = null;
            fixture.Scene.CoveragePullObserverForTests = null;
            staging?.Dispose(); staging = null;
        }
    }

    void Record(IGpuCommandList commands, SkinnedCoverageFrame[] frames)
    {
        var scene = fixture.Scene;
        var batch = scene.CoverageGpuBatchForTests;
        surfaces = frames.Where(frame => frame.Preparation is not null).Select(frame =>
        {
            var p = frame.Preparation!;
            var staged = scene.GetCoverageOutcome(frame);
            bool dispatched = staged.Presentation == CoveragePresentation.Dispatched;
            var actorPulls = pulls.Where(pull => pull.Draw.Actor == p.Actor).ToArray();
            foreach (var pull in actorPulls)
            {
                Assert.True(dispatched);
                Assert.Same(batch!.Current, pull.Draw.Surface.Current);
                Assert.Same(batch.Previous, pull.Draw.Surface.Previous);
                Assert.Same(batch.StaticData, pull.Draw.Surface.StaticData);
            }
            var consumers = actorPulls.Select(pull => Consumer(pull.Pass, pull.Draw)).ToArray();
            return new CoverageSceneSurfaceEvidence(p.Generation, p.View, p.Actor.Value, p.Id, staged.Presentation,
                null, dispatched ? p.GpuReservation : default, dispatched ? Id(batch!.Current) : 0,
                dispatched ? Id(batch!.Previous) : 0, dispatched ? Id(batch!.StaticData) : 0,
                dispatched ? "pending-gpu" : "cpu-fallback", "", [], [], [], consumers)
            { Admission = staged };
        }).ToArray();
        if (!surfaces.Any(surface => surface.StagedPresentation == CoveragePresentation.Dispatched)) return;
        Assert.False(scene.TryReadCoverageOutcome(out _));
        submittedCurrent = batch!.Current; submittedPrevious = batch.Previous;
        staging = fixture.Device.Factory.CreateBuffer(new(submittedCurrent.SizeInBytes, GpuBufferUsage.Staging));
        commands.CopyBuffer(submittedCurrent, 0, staging, 0, submittedCurrent.SizeInBytes);
    }

    unsafe void Complete()
    {
        uint[] words = [];
        if (staging is not null)
        {
            // Submitted swaps the logical roles. The copied object remains the one actually consumed by draws.
            Assert.Same(submittedCurrent, fixture.Scene.CoverageGpuBatchForTests!.Previous);
            Assert.Same(submittedPrevious, fixture.Scene.CoverageGpuBatchForTests.Current);
            var map = fixture.Device.Map(staging, GpuMapMode.Read);
            try { words = new ReadOnlySpan<uint>((void*)map.Data, checked((int)staging.SizeInBytes / 4)).ToArray(); }
            finally { fixture.Device.Unmap(staging); }
        }
        var completed = new List<CoverageGpuOutcome>();
        while (fixture.Scene.TryReadCoverageOutcome(out var outcome)) completed.Add(outcome);
        Assert.Equal(surfaces.Count(surface => surface.StagedPresentation == CoveragePresentation.Dispatched), completed.Count);
        Surfaces = surfaces.Select(surface =>
        {
            if (surface.StagedPresentation != CoveragePresentation.Dispatched)
            { Assert.Empty(surface.Consumers); return surface; }
            var r = surface.Reservation;
            var header = words.AsSpan(checked((int)r.CurrentBase), 16).ToArray();
            var source = words.AsSpan(checked((int)r.CurrentBase + 16), r.SourceCount * 16).ToArray();
            var final = words.AsSpan(checked((int)r.CurrentBase + 16 + r.SourceCount * 16), r.VertexCount * 16).ToArray();
            bool refused = header[14] != 0;
            var actual = surface with
            {
                CompletedOutcome = completed.Single(value => value.FrameId == surface.FrameId && value.ViewId == surface.ViewId
                    && value.Actor.Value == surface.Actor),
                HeaderWords = header,
                SourceWords = source,
                FinalWords = final,
                Selection = refused ? "source" : "final",
                Signature = Hash(refused ? source : final),
                SubmittedBufferRole = "previous-after-submit",
            };
            AssertAssociation(actual);
            if (refused) Assert.All(final, word => Assert.Equal(0u, word));
            return actual;
        }).ToArray();
    }

    internal static void AssertAssociation(CoverageSceneSurfaceEvidence surface)
    {
        var outcome = Assert.IsType<CoverageGpuOutcome>(surface.CompletedOutcome);
        var header = surface.HeaderWords;
        Assert.Equal((surface.FrameId, surface.ViewId, surface.Actor, surface.PreparationId),
            (Pair(0), Pair(2), Pair(4), Pair(6)));
        Assert.Equal((surface.FrameId, surface.ViewId, surface.Actor), (outcome.FrameId, outcome.ViewId, outcome.Actor.Value));
        Assert.Equal(CoveragePresentation.Dispatched, surface.StagedPresentation);
        Assert.Equal(header[14] == 0 ? "final" : "source", surface.Selection);
        Assert.Equal("previous-after-submit", surface.SubmittedBufferRole);
        Assert.Equal((uint)outcome.Outcome.Failure.Code, header[8]);
        Assert.Equal(outcome.Outcome.Presentation == CoveragePresentation.Covered ? 0u : 1u, header[14]);
        Assert.Equal(outcome.Outcome.Failure.EquipmentInstanceId, Pair(9));
        Assert.Equal(outcome.Outcome.Failure.CommandId, header[11]);
        Assert.Equal((uint)outcome.Outcome.Failure.Count, header[12]);
        Assert.Equal((uint)outcome.Outcome.Failure.Limit, header[13]);
        Assert.Equal(Hash(surface.Selection == "source" ? surface.SourceWords : surface.FinalWords), surface.Signature);
        Assert.All(surface.Consumers, consumer =>
        {
            Assert.Equal((surface.CurrentBufferId, surface.PreviousBufferId, surface.StaticBufferId),
                (consumer.CurrentBufferId, consumer.PreviousBufferId, consumer.StaticBufferId));
            Assert.Equal((surface.Reservation.CurrentBase, surface.Reservation.PreviousBase, surface.Reservation.StaticBase),
                (consumer.CurrentBase, consumer.PreviousBase, consumer.StaticBase));
            Assert.Equal((surface.Reservation.VertexCount, surface.Reservation.TriangleCount, surface.Actor),
                (consumer.VertexCount, consumer.TriangleCount, consumer.Actor));
            Assert.InRange(consumer.FirstCorner, 0, consumer.TriangleCount * 3 - 1);
            Assert.InRange(consumer.CornerCount, 1, consumer.TriangleCount * 3 - consumer.FirstCorner);
        });
        ulong Pair(int index) => header[index] | (ulong)header[index + 1] << 32;
    }

    static CoverageSceneConsumer Consumer(string pass, CoveragePulledDraw draw) => new(pass,
        Id(draw.Surface.Current), Id(draw.Surface.Previous), Id(draw.Surface.StaticData), draw.Surface.CurrentBase,
        draw.Surface.PreviousBase, draw.Surface.StaticBase, draw.Surface.VertexCount, draw.Surface.TriangleCount,
        draw.FirstCorner, draw.CornerCount, draw.InstanceSlot, draw.Actor.Value, draw.VisibleMain, draw.ShadowKind.ToString());

    static int Id(IGpuBuffer buffer) => RuntimeHelpers.GetHashCode(buffer);
    internal static string Hash(uint[] words) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(words.AsSpan())));
    public void Dispose() => staging?.Dispose();
}
