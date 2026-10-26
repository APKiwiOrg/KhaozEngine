using System;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipStageBoundaryTests
{
    [Theory]
    [InlineData("plan-growth", 3584, 2304, 1)]
    [InlineData("plan-freeze", 4224, 640, 1)]
    [InlineData("compiler", 2576, 16, 2)]
    [InlineData("arithmetic", 99652, 49, 16)]
    [InlineData("raw-transfer", 34816, 256, 1)]
    [InlineData("candidate-row", 50688, 16, 32)]
    public void IndependentBoundary_AdmitsExactCapacityAndRefusesOneByteBeforeAllocation(
        string factory, long threshold, long taggedIncrement, int entries)
    {
        Check(true);
        Check(false);

        void Check(bool exact)
        {
            var operation = Operation(factory);
            var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000000));
            using var old = admission.Workspace();
            old.Reserve(500000); old.Release(500000); old.Reserve(100);
            using var retained = old.Transfer(100);
            using var window = admission.LimitPreparation(exact ? threshold : threshold - 1, "StageWindow", 50);
            var counts = new MapRoofPreparationCounts();
            int entered = 0;
            admission.AllocationAdmitted = increment => { if (increment == taggedIncrement) entered++; };
            Exception? error;
            using (var interval = admission.ObservePeak(counts.Membership))
            using (var active = admission.Enter())
            using (var product = admission.BeginPreparation())
            using (var build = new MapMembershipBuild(admission, product, counts))
                error = Record.Exception(() => operation(admission, counts));

            if (!exact || factory == "plan-growth")
            {
                long requested = exact ? 4224 : threshold;
                var refusal = Assert.IsType<MapSceneCapacityException>(error);
                Assert.Equal(new MapSceneCapacity("StageWindow", 50 + requested,
                    50 + (exact ? threshold : threshold - 1)), refusal.Capacity);
                Assert.Equal(100 + requested, counts.Membership.Root.RefusedBytes);
                Assert.Equal(50 + requested, counts.Membership.BindingWindow!.RefusedBytes);
            }
            else Assert.Null(error);
            int expectedEntries = exact ? entries : factory is "arithmetic" or "candidate-row" ? entries - 1 : 0;
            Assert.Equal(expectedEntries, entered);
            if (exact)
            {
                Assert.Equal(100 + threshold, counts.Membership.Root.AdmittedPeakBytes);
                Assert.Equal(50 + threshold, counts.Membership.BindingWindow!.AdmittedPeakBytes);
            }
            else
            {
                Assert.Equal(100 + threshold, counts.Membership.Root.RequestedBytes);
                Assert.True(counts.Membership.Root.AdmittedPeakBytes < 100 + threshold);
            }
            Assert.Equal(0, admission.WorkspaceBytes);
            Assert.Equal(100, admission.RetainedBytes);
            Assert.Equal(500000, admission.PeakBytes);
            if (factory == "compiler") Assert.Equal(2, admission.PreparedFaces);
        }
    }

    static Action<MapSceneAdmission, MapRoofPreparationCounts> Operation(string factory)
    {
        if (factory is "plan-growth" or "plan-freeze")
        {
            var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
            surface = surface with { IndoorSpan = new("indoor", new("world", patch.Key), 0, 300, []) };
            patch.Cells[0] = patch.Cells[0] with { Flags = MapCellFlags.Indoor };
            var set = new MapSurfaceSet();
            set.Refs.Add(surface); set.Patches.Add(patch.Key, patch);
            var view = MapScopedSurfaces.CompleteView(set);
            // One planner control, one patch-index row, four 64-word builder planes and one frozen mask.
            return (admission, counts) =>
            {
                using var owner = admission.Workspace();
                _ = new MapMembershipDemandPlan(view, [], owner);
            };
        }
        if (factory == "compiler")
        {
            var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
            // Two face envelopes, with a four-int subdivision table before each compiler pass.
            return (admission, counts) => { _ = MapSurfaceCompiler.Compile(surface, patch); };
        }
        if (factory == "arithmetic")
        {
            MapExactXz[] square = [new(new(0, 1), new(0, 1)), new(new(1, 1), new(0, 1)),
                new(new(1, 1), new(1, 1)), new(new(0, 1), new(1, 1))];
            // Polygon base 308, four 260-byte affine outputs and three nested 32768-byte kernels.
            return (admission, counts) => { _ = new MapMembershipPolygon(square); };
        }
        if (factory == "raw-transfer")
            return (admission, counts) =>
            {
                using var validation = new MapPrismValidationWorkspace(admission, counts, 0);
                using var candidate = MapPrismValidationWorkspace.RawCandidate(3);
                MapPrismValidationWorkspace.PublishRaw(candidate, 4);
                MapPrismValidationWorkspace.AddedRawPiece(4);
                Assert.Equal(32768 + 2048, admission.WorkspaceBytes);
                Assert.Equal(0, candidate!.Bytes);
                Assert.Equal(1, counts.ValidationRawPieces);
                Assert.Equal(4, counts.ValidationVertices);
            };
        if (factory == "candidate-row")
        {
            var domain = MapWaterDomain.Compile(new(new(new(1, 64), new(1, 100),
                MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
                Enumerable.Range(0, 32).Select(i => new MapWaterRect(2 * i, 0, 2 * i + 1, 64)).ToArray(), []));
            var bounds = new MapWaterPolygons.Bounds(MapWideXz.From(new(new(0, 1), new(0, 1))),
                MapWideXz.From(new(new(1, 1), new(1, 1))));
            // Validation header, 32-piece index, six traversal slots and a 32-entry result growth envelope.
            return (admission, counts) =>
            {
                using var validation = new MapPrismValidationWorkspace(admission, counts, 0);
                using var candidates = new MapPrismRoomCandidates(domain, [bounds]);
                int count = 0;
                foreach (int piece in candidates.Pieces(0))
                {
                    Assert.Equal(count++, piece);
                    Assert.Equal(100 + 128, admission.RetainedBytes);
                }
                Assert.Equal(32, count);
                Assert.Equal(100, admission.RetainedBytes);
            };
        }
        throw new ArgumentOutOfRangeException(nameof(factory));
    }
}
