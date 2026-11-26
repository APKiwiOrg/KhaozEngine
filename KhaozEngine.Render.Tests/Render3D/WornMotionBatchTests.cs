using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Simulation;
using Xunit;
using AllocAssert = KhaozEngine.Tests.AllocationMeasurement;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class WornMotionBatchTests
{
    const float FrameTime = 1f / 144;

    [Fact]
    public void Duplicate_cloth_slot_refuses_before_publishing_a_wearer_surface()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("batch-cloth.json", SkinnedAttachmentBindingTests.ClothAsset(rig).Set);
        const uint part = SkinnedAttachmentBindingTests.ClothPartId;
        var first = set.CreateMotion(null);
        var second = set.CreateMotion(null);
        var slot = new WornClothSlot();
        slot.Prepare(set.ClothSurface(part)!);
        var selection = new WornClothSelection(slot, 0, part);
        var batch = new WornMotionBatch(new SingleThreadedJobScheduler());
        batch.Add(first, rig.Skeleton, rig.Palette, rig.Model, 0, MotionLevel.Held, selection);
        batch.Add(second, rig.Skeleton, rig.Palette, rig.Model, 0, MotionLevel.Held, selection);
        var error = Assert.Throws<AttachmentSubmissionArgumentException>(batch.Step);
        Assert.Equal(AttachmentSubmissionRefusal.DuplicateClothSlot, error.Reason);
        Assert.Equal(1, error.EntryIndex);
        Assert.False(first.TryGetClothPresentation(0, part, out _));
        Assert.False(second.TryGetClothPresentation(0, part, out _));

        batch.Clear();
        batch.Add(first, rig.Skeleton, rig.Palette, rig.Model, 0, MotionLevel.Held, selection);
        batch.Step();
        Assert.True(first.TryGetClothPresentation(0, part, out _));
    }

    [Fact]
    public void Cloth_batch_matches_direct_pose_display_and_support_work()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("pair-cloth.json", SkinnedAttachmentBindingTests.ClothAsset(rig).Set);
        const uint part = SkinnedAttachmentBindingTests.ClothPartId;
        var direct = set.CreateMotion(null);
        var batched = set.CreateMotion(null);
        var other = set.CreateMotion(null);
        var firstSlot = new WornClothSlot();
        var secondSlot = new WornClothSlot();
        var surface = set.ClothSurface(part)!;
        firstSlot.Prepare(surface);
        secondSlot.Prepare(surface);
        using var workers = new WorkerJobScheduler(2);
        var batch = new WornMotionBatch(workers);
        var pose = (Matrix4x4[])rig.Palette.Clone();

        for (int i = 0; i < 24; i++)
        {
            float step = i is >= 8 and < 16 ? .003f : 0;
            pose[1].Translation += new Vector3(0, 0, step);
            var model = Matrix4x4.CreateRotationY(i * .001f);
            batch.Add(other, rig.Skeleton, rig.Palette, rig.Model, FrameTime);
            batch.Add(batched, rig.Skeleton, pose, model, FrameTime, MotionLevel.Simulated, new(secondSlot, 0, part));
            direct.Step(rig.Skeleton, pose, model, FrameTime, MotionLevel.Simulated, new(firstSlot, 0, part));
            batch.Step();
            batch.Clear();

            Assert.Equal(direct.ClothStatus, batched.ClothStatus);
            Assert.True(direct.TryGetClothPresentation(0, part, out var expected));
            Assert.True(batched.TryGetClothPresentation(0, part, out var actual));
            Assert.True(MemoryMarshal.AsBytes(expected.Nodes).SequenceEqual(MemoryMarshal.AsBytes(actual.Nodes)));
            Assert.True(direct.JointsFromAnchor.SequenceEqual(batched.JointsFromAnchor));
            Assert.False(other.TryGetClothPresentation(0, part, out _));
        }
    }

    [Fact]
    public void Late_invalid_cloth_pose_leaves_the_current_display_and_both_clocks_unchanged()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("atomic-cloth.json", SkinnedAttachmentBindingTests.ClothAsset(rig).Set);
        const uint part = SkinnedAttachmentBindingTests.ClothPartId;
        var surface = set.ClothSurface(part)!;
        var first = set.CreateMotion(null);
        var second = set.CreateMotion(null);
        var slot = new WornClothSlot();
        var otherSlot = new WornClothSlot();
        slot.Prepare(surface);
        otherSlot.Prepare(surface);
        first.Step(rig.Skeleton, rig.Palette, rig.Model, 0, MotionLevel.Simulated, new(slot, 0, part));
        Assert.True(first.TryGetClothPresentation(0, part, out var before));
        ulong revision = before.DisplayRevision;
        ulong secondPose = second.PoseRevision;

        var invalid = (Matrix4x4[])rig.Palette.Clone();
        invalid[0].M43 = float.NaN;
        var batch = new WornMotionBatch(new SingleThreadedJobScheduler());
        batch.Add(first, rig.Skeleton, rig.Palette, rig.Model, FrameTime, MotionLevel.Simulated, new(slot, 0, part));
        batch.Add(second, rig.Skeleton, invalid, rig.Model, FrameTime, MotionLevel.Simulated, new(otherSlot, 0, part));
        var error = Assert.Throws<AttachmentBindingArgumentException>(batch.Step);
        Assert.Equal(AttachmentBindingRefusal.HostJointPoseInvalid, error.Reason);
        Assert.False(otherSlot.IsOccupied);
        Assert.Equal(secondPose, second.PoseRevision);
        Assert.True(first.TryGetClothPresentation(0, part, out var after));
        Assert.Equal(revision, after.DisplayRevision);
        Assert.Equal(revision, first.PoseRevision);
    }

    [Fact]
    public void A_valid_cloth_claim_after_an_unused_bad_host_bone_seeds_current_fit_in_a_batch()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("prior-cloth-pose.json", SkinnedAttachmentBindingTests.ClothAsset(rig).Set);
        const uint part = SkinnedAttachmentBindingTests.ClothPartId;
        var surface = set.ClothSurface(part)!;
        var first = set.CreateMotion(null);
        var selected = set.CreateMotion(null);
        var slot = new WornClothSlot();
        slot.Prepare(surface);
        var unusedKnee = (Matrix4x4[])rig.Palette.Clone();
        unusedKnee[0].M43 = float.NaN;
        selected.Step(rig.Skeleton, unusedKnee, rig.Model, 0, MotionLevel.Held);
        ulong previousHistory = selected.HistoryRevision;
        ulong firstRevision = first.PoseRevision;

        var batch = new WornMotionBatch(new SingleThreadedJobScheduler());
        batch.Add(first, rig.Skeleton, rig.Palette, rig.Model, FrameTime);
        batch.Add(selected, rig.Skeleton, rig.Palette, rig.Model, FrameTime, MotionLevel.Held, new(slot, 0, part));
        batch.Step();

        Assert.True(first.PoseRevision > firstRevision);
        Assert.True(selected.HistoryRevision > previousHistory);
        Assert.Equal(0, selected.ClothStatus.Substeps);
        Assert.True(selected.TryGetClothPresentation(0, part, out var shown));
        Assert.True(shown.IsCurrentPose);
        Assert.True(Matrix4x4.Invert(shown.RelativeModel, out var toModel));
        for (int i = 0; i < shown.Nodes.Length; i++)
            Assert.InRange(Vector3.Distance(surface.FittedNodes[i], Vector3.Transform(shown.Nodes[i], toModel)), 0, .0005f);
    }

    [Fact]
    public void An_invalid_old_rigid_hull_basis_reseeds_before_the_batch_advances()
    {
        using var rig = new OutfitRig();
        const uint part = SkinnedAttachmentBindingTests.ClothPartId;
        var cloth = rig.Wear("basis-coat.json", SkinnedAttachmentBindingTests.ClothAsset(rig).Set);
        var origin = new Vector3(0, .5f, .3f);
        var hull = ClothSupportHull.Create([origin, origin + .1f * Vector3.UnitX, origin + .1f * Vector3.UnitY,
            origin + .1f * Vector3.UnitZ], [new(0, 2, 1), new(0, 1, 3), new(0, 3, 2), new(1, 2, 3)]);
        var hardware = OutfitRig.Rigid(9, rig.Source.Mesh("basis-hull.glb", AttachmentTestSource.Glb()), "alpha") with
        {
            Joint = "knee",
            ClothHulls = [new(1, hull, [0])],
        };
        var lower = rig.Wear("basis-hull.json", new AttachmentSetDefinition([hardware], AttachmentLayer.Garment, [],
            [new("knee", origin, origin + .1f * Vector3.UnitY, .03f)]));
        using var outfit = rig.Cache.BindOutfit(OutfitLiftDrawTests.Body(rig), [lower, cloth]);
        var first = cloth.CreateMotion(null);
        var selected = outfit.CreateMotion(null);
        var slot = new WornClothSlot();
        slot.Prepare(outfit.ClothSurface(1, part)!);
        var cheap = (Matrix4x4[])rig.Palette.Clone();
        // Invertible for chain capsules, but too short for the rigid draw's Gram-Schmidt basis.
        cheap[0].M11 = 1e-7f;
        selected.Step(rig.Skeleton, cheap, rig.Model, 0, MotionLevel.Held);
        ulong firstRevision = first.PoseRevision;
        ulong priorHistory = selected.HistoryRevision;

        var batch = new WornMotionBatch(new SingleThreadedJobScheduler());
        batch.Add(first, rig.Skeleton, rig.Palette, rig.Model, FrameTime);
        batch.Add(selected, rig.Skeleton, rig.Palette, rig.Model, FrameTime, MotionLevel.Held, new(slot, 1, part));
        batch.Step();

        Assert.True(first.PoseRevision > firstRevision);
        Assert.True(selected.HistoryRevision > priorHistory);
        Assert.Equal(0, selected.ClothStatus.Substeps);
        Assert.True(selected.TryGetClothPresentation(1, part, out var shown));
        Assert.True(shown.IsCurrentPose);
    }

    [Fact]
    public void Warm_local_cloth_and_compatible_slot_turnover_allocate_nothing()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("allocation-cloth.json", SkinnedAttachmentBindingTests.ClothAsset(rig).Set);
        const uint part = SkinnedAttachmentBindingTests.ClothPartId;
        var slot = new WornClothSlot();
        var wearer = set.CreateMotion(null);
        var rebound = set.CreateMotion(null);
        var ordinary = set.CreateMotion(null);
        Assert.False(slot.IsPrepared);
        ordinary.Step(rig.Skeleton, rig.Palette, rig.Model, FrameTime);
        Assert.False(slot.IsPrepared);
        Assert.Equal(0, ordinary.ClothStatus.Substeps);

        slot.Prepare(set.ClothSurface(part)!);
        var selection = new WornClothSelection(slot, 0, part);
        wearer.Step(rig.Skeleton, rig.Palette, rig.Model, 0, MotionLevel.Held, selection);
        for (int i = 0; i < 8; i++) Frame();
        AllocAssert.NoPerCallAllocation("warm cloth and compatible outfit turnover", () =>
        {
            for (int i = 0; i < 16; i++) Frame();
        });
        Assert.True(wearer.TryGetClothPresentation(0, part, out _));

        void Frame()
        {
            _ = slot.Rebind(rebound, 0, part);
            (wearer, rebound) = (rebound, wearer);
            wearer.Step(rig.Skeleton, rig.Palette, rig.Model, 0, MotionLevel.Held, selection);
            wearer.Step(rig.Skeleton, rig.Palette, rig.Model, FrameTime, MotionLevel.Held, selection);
            ordinary.Step(rig.Skeleton, rig.Palette, rig.Model, FrameTime);
        }
    }

    [Fact]
    public void Warm_selected_cloth_allocates_nothing_on_a_dedicated_scheduler_worker()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("worker-cloth.json", SkinnedAttachmentBindingTests.ClothAsset(rig).Set);
        const uint part = SkinnedAttachmentBindingTests.ClothPartId;
        var local = set.CreateMotion(null);
        var ordinary = set.CreateMotion(null);
        var slot = new WornClothSlot();
        slot.Prepare(set.ClothSurface(part)!);
        using var scheduler = new ClothWorkerAllocationScheduler();
        var batch = new WornMotionBatch(scheduler);
        var selection = new WornClothSelection(slot, 0, part);
        for (int i = 0; i < 12; i++) Frame();
        scheduler.StartCounting();
        AllocAssert.NoPerCallAllocation("warm selected cloth worker", () =>
        {
            for (int i = 0; i < 16; i++) Frame();
        });
        Assert.True(scheduler.UsedDedicatedWorker);
        Assert.Equal(0, scheduler.WorkerBytes);
        Assert.True(local.ClothStatus.Substeps > 0);
        Assert.True(local.TryGetClothPresentation(0, part, out _));

        void Frame()
        {
            batch.Clear();
            batch.Add(ordinary, rig.Skeleton, rig.Palette, rig.Model, FrameTime, MotionLevel.Held);
            batch.Add(local, rig.Skeleton, rig.Palette, rig.Model, FrameTime, MotionLevel.Held, selection);
            batch.Step();
        }
    }

    [Fact]
    public void Slot_replacement_waits_for_the_outgoing_surface_before_the_next_wearer_solves()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("reassign-cloth.json", SkinnedAttachmentBindingTests.ClothAsset(rig).Set);
        const uint part = SkinnedAttachmentBindingTests.ClothPartId;
        var slot = new WornClothSlot();
        slot.Prepare(set.ClothSurface(part)!);
        var options = new ChainSimulationOptions { HoldBlendSeconds = .025f };
        var first = set.CreateMotion(null, options);
        var second = set.CreateMotion(null, options);
        var request = new WornClothSelection(slot, 0, part);
        first.Step(rig.Skeleton, rig.Palette, rig.Model, 0, MotionLevel.Held, request);
        for (int i = 0; i < 6; i++)
            first.Step(rig.Skeleton, rig.Palette, rig.Model, FrameTime, MotionLevel.Held, request);
        Assert.Equal(WornClothTier.Cloth, first.ClothStatus.Tier);

        using var workers = new WorkerJobScheduler(2);
        var batch = new WornMotionBatch(workers);
        bool reassigned = false;
        for (int i = 0; i < 6; i++)
        {
            batch.Clear();
            batch.Add(second, rig.Skeleton, rig.Palette, rig.Model, FrameTime, MotionLevel.Held, request);
            batch.Add(first, rig.Skeleton, rig.Palette, rig.Model, FrameTime, MotionLevel.Held);
            batch.Step();
            Assert.Equal(0, first.ClothStatus.Substeps);
            if (first.TryGetClothPresentation(0, part, out _))
            {
                Assert.Equal(WornClothFallback.SlotOccupied, second.ClothStatus.Fallback);
                Assert.False(second.TryGetClothPresentation(0, part, out _));
            }
            else
                reassigned |= second.TryGetClothPresentation(0, part, out _);
        }
        Assert.True(reassigned);
    }

    [Fact]
    public void Cloth_wearer_selection_retains_incumbent_and_breaks_distance_ties_by_stable_id()
    {
        WornClothCandidate[] candidates = [new(20, Vector3.UnitX, true), new(1, Vector3.UnitY, true),
            new(3, Vector3.UnitZ * 2, true), new(0, Vector3.Zero, false, true)];
        Assert.Equal(3ul, WornClothWearerChoice.Select(candidates, Vector3.Zero, 1, 2, 3));
        Assert.Equal(1ul, WornClothWearerChoice.Select(candidates, Vector3.Zero, 1, 2, 0));
        candidates[0] = candidates[0] with { Preferred = true };
        Assert.Equal(20ul, WornClothWearerChoice.Select(candidates, Vector3.Zero, 1, 2, 3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Layered_batch_matches_every_sequential_draw_bit_for_bit(bool parallel)
    {
        const int wearers = 4, frames = 288;
        using var workers = parallel ? new WorkerJobScheduler(3) : null;
        var batch = new WornMotionBatch((IJobScheduler?)workers ?? new SingleThreadedJobScheduler());
        using var rig = new OutfitRig();
        using WearerPair first = new(rig, 0, wearers, useSheetFixture: true);
        using WearerPair second = new(rig, 1, wearers, useSheetFixture: true);
        using WearerPair third = new(rig, 2, wearers, useSheetFixture: true);
        using WearerPair fourth = new(rig, 3, wearers, useSheetFixture: true);
        WearerPair[] pairs = [first, second, third, fourth];

        for (int frame = 0; frame < frames; frame++)
        {
            float seconds = frame * FrameTime;
            var level = frame >= frames / 3 && frame < frames * 2 / 3 ? MotionLevel.Held : MotionLevel.Simulated;
            foreach (WearerPair pair in pairs)
            {
                pair.Inline.Pose(seconds);
                pair.Batched.Pose(seconds);
                batch.Add(pair.Batched.Motion, rig.Skeleton, pair.Batched.Palette, pair.Batched.Model, FrameTime, level);
                pair.Inline.Motion.Step(rig.Skeleton, pair.Inline.Palette.Span, pair.Inline.Model, FrameTime, level);
            }
            batch.Step();
            batch.Clear();
            foreach (WearerPair pair in pairs) pair.AssertSameDraws();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Invalid_entry_refuses_the_whole_batch_before_advancing_motion(int invalid)
    {
        using var rig = new OutfitRig();
        using WearerPair first = new(rig, 0, 2);
        using WearerPair second = new(rig, 1, 2);
        WearerPair[] pairs = [first, second];
        var batch = new WornMotionBatch(new SingleThreadedJobScheduler());
        foreach (WearerPair pair in pairs) pair.Prime();

        for (int i = 0; i < pairs.Length; i++)
        {
            SwingCostWearer wearer = pairs[i].Batched;
            batch.Add(wearer.Motion, rig.Skeleton, wearer.Palette, wearer.Model, i == invalid ? -FrameTime : FrameTime);
        }
        var error = Assert.Throws<ArgumentOutOfRangeException>(batch.Step);
        Assert.Equal("deltaSeconds", error.ParamName);
        foreach (WearerPair pair in pairs) pair.AssertSameDraws();

        batch.Clear();
        foreach (WearerPair pair in pairs)
        {
            SwingCostWearer wearer = pair.Batched;
            batch.Add(wearer.Motion, rig.Skeleton, wearer.Palette, wearer.Model, FrameTime);
            pair.Inline.Step(FrameTime);
        }
        batch.Step();
        foreach (WearerPair pair in pairs) pair.AssertSameDraws();
    }

    [Fact]
    public void Repeated_motion_names_the_duplicate_entry_and_advances_nothing()
    {
        using var rig = new OutfitRig();
        using WearerPair pair = new(rig, 0, 1);
        pair.Prime();
        var batch = new WornMotionBatch(new SingleThreadedJobScheduler());
        SwingCostWearer wearer = pair.Batched;
        batch.Add(wearer.Motion, rig.Skeleton, wearer.Palette, wearer.Model, FrameTime);
        batch.Add(wearer.Motion, rig.Skeleton, wearer.Palette, wearer.Model, FrameTime);

        var error = Assert.Throws<AttachmentSubmissionArgumentException>(batch.Step);
        Assert.Equal(AttachmentSubmissionRefusal.DuplicateMotion, error.Reason);
        Assert.Equal(1, error.EntryIndex);
        pair.AssertSameDraws();

        Matrix4x4[] beforeStep = wearer.Motion.JointsFromAnchor.ToArray();
        batch.Clear();
        batch.Add(wearer.Motion, rig.Skeleton, wearer.Palette, wearer.Model, FrameTime);
        pair.Inline.Step(FrameTime);
        batch.Step();
        pair.AssertSameDraws();
        Assert.False(beforeStep.AsSpan().SequenceEqual(wearer.Motion.JointsFromAnchor));
    }

    [Fact]
    public void Warm_batch_fill_and_step_allocate_nothing_on_the_caller()
    {
        using var rig = new OutfitRig();
        using WearerPair pair = new(rig, 0, 2);
        pair.Prime();
        using var scheduler = new WorkerJobScheduler(1);
        var batch = new WornMotionBatch(scheduler);

        void FillAndStep()
        {
            batch.Clear();
            batch.Add(pair.Batched.Motion, rig.Skeleton, pair.Batched.Palette, pair.Batched.Model, FrameTime);
            batch.Add(pair.Inline.Motion, rig.Skeleton, pair.Inline.Palette, pair.Inline.Model, FrameTime);
            batch.Step();
        }

        for (int frame = 0; frame < 16; frame++) FillAndStep();
        AllocAssert.NoPerCallAllocation("worn motion batch fill and step", () =>
        {
            for (int frame = 0; frame < 16; frame++) FillAndStep();
        });
    }

    sealed class WearerPair : IDisposable
    {
        internal SwingCostWearer Inline { get; }
        internal SwingCostWearer Batched { get; }

        internal WearerPair(OutfitRig rig, int index, int wearers, bool useSheetFixture = false)
        {
            var body = rig.BodyRegions(new RegionRun(0, 4));
            var colliders = SwingCostFixture.LoadColliders(rig);
            var pieces = useSheetFixture
                ? LayeredSwingCostOutfit.LoadSheetCase(rig, sevenGuides: false)
                : LayeredSwingCostOutfit.Load(rig);
            var options = new ChainSimulationOptions();
            Inline = new SwingCostWearer(rig, body, pieces, colliders, options, index, wearers);
            try
            {
                Batched = new SwingCostWearer(rig, body, pieces, colliders, options, index, wearers);
            }
            catch
            {
                Inline.Dispose();
                throw;
            }
            Inline.Reset(MotionLevel.Simulated);
            Batched.Reset(MotionLevel.Simulated);
        }

        internal void Prime()
        {
            for (int frame = 0; frame < 8; frame++)
            {
                Inline.Pose(frame * FrameTime);
                Batched.Pose(frame * FrameTime);
                Inline.Step(FrameTime);
                Batched.Step(FrameTime);
            }
            Inline.Pose(8 * FrameTime);
            Batched.Pose(8 * FrameTime);
        }

        internal void AssertSameDraws()
        {
            ReadOnlySpan<Matrix4x4> expectedFrames = Inline.Motion.JointsFromAnchor;
            ReadOnlySpan<Matrix4x4> actualFrames = Batched.Motion.JointsFromAnchor;
            Assert.True(expectedFrames.Length > 0);
            Assert.Equal(expectedFrames.Length, actualFrames.Length);
            for (int joint = 0; joint < expectedFrames.Length; joint++)
                AssertBits(expectedFrames[joint], actualFrames[joint]);

            Inline.WriteDraws();
            Batched.WriteDraws();
            Assert.Equal(3, Inline.Draws.Length);
            Assert.Equal(Inline.Draws.Length, Batched.Draws.Length);
            for (int worn = 0; worn < Inline.Draws.Length; worn++)
            {
                AttachmentDraw expected = Assert.Single(Inline.Draws[worn]);
                AttachmentDraw actual = Assert.Single(Batched.Draws[worn]);
                Assert.Equal(expected.IsSkinned, actual.IsSkinned);
                if (expected.IsSkinned)
                {
                    AssertBits(expected.Skinned.Model, actual.Skinned.Model);
                    ReadOnlySpan<Matrix4x4> expectedBones = expected.BoneMatrices.Span;
                    ReadOnlySpan<Matrix4x4> actualBones = actual.BoneMatrices.Span;
                    Assert.True(expectedBones.Length > 2);
                    Assert.Equal(expectedBones.Length, actualBones.Length);
                    for (int joint = 0; joint < expectedBones.Length; joint++)
                        AssertBits(expectedBones[joint], actualBones[joint]);
                }
                else
                {
                    AssertBits(expected.Rigid.World, actual.Rigid.World);
                }
            }
        }

        static void AssertBits(in Matrix4x4 expected, in Matrix4x4 actual)
        {
            if (MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(in expected, 1)).SequenceEqual(
                MemoryMarshal.AsBytes(
                    MemoryMarshal.CreateReadOnlySpan(in actual, 1)))) return;

            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    Assert.Equal(BitConverter.SingleToInt32Bits(expected[row, column]),
                        BitConverter.SingleToInt32Bits(actual[row, column]));
        }

        public void Dispose()
        {
            Batched.Dispose();
            Inline.Dispose();
        }
    }
}
