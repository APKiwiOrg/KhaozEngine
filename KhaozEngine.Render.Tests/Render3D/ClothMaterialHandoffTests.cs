using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.Render3D.SkinnedAttachmentBindingTests;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class ClothMaterialHandoffTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Material_handoff_matches_the_lifted_chain_pose_and_preserves_mid_blend_reversal(bool reuseMaterialFactor)
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig, hipOnlyPins: true);
        var part = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(fixture.Set.Parts));
        var recipe = Assert.IsType<ClothMotionDefinition>(part.Motion);
        ClothSurfaceFile file = ClothSurfaceJson.Parse(fixture.Bytes, recipe.Surface.Key);
        var fit = file.Render[2];
        var cloth = BindMaterialCloth(rig, fixture.Set, part, recipe, file with
        {
            MaterialRender = [fit with { SignedOffset = 2 * fit.SignedOffset }],
        });
        BoundAttachmentSet lower = OutfitLiftDrawTests.Garment(rig, standoff: .003f);
        using BoundOutfit outfit = rig.Cache.BindOutfit(OutfitLiftDrawTests.Body(rig), [lower, cloth]);
        const int worn = 1;
        var surface = outfit.ClothSurface(worn, ClothPartId)!;
        var options = new ChainSimulationOptions { HoldBlendSeconds = 1f / 12 };
        var motion = outfit.CreateMotion(null, options);
        var slot = reuseMaterialFactor ? new WornClothSlot(reuseMaterialFactor: true) : new WornClothSlot();
        slot.Prepare(surface);
        var selection = new WornClothSelection(slot, worn, ClothPartId);
        var pose = (Matrix4x4[])rig.Palette.Clone();
        var draw = new AttachmentDraw[outfit.DrawCount(worn)];
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity);
        Matrix4x4 model = Matrix4x4.CreateTranslation(3, 0, 1);

        Step(0, null);
        Vector3[][] rest = Cheap(motion);
        for (int i = 0; i < 6; i++)
        {
            pose[1].Translation += new Vector3(.001f, 0, .0015f);
            Step(options.MaxStepSeconds, null);
        }
        Vector3[][] swing = Cheap(motion);
        float cheapTravel = Distance(rest, swing, movingOnly: true);
        float swingDisplacement = Distance(Cheap(null), swing, movingOnly: true);
        Assert.True(swingDisplacement > ClothSurfaceFile.RoundTripTolerance);

        Step(0, selection);
        float promotion = AssertClose(swing, Shown(), movingOnly: true);
        for (int i = 0; i < 12; i++) Step(options.MaxStepSeconds, selection);
        Assert.Equal(WornClothTier.Cloth, motion.ClothStatus.Tier);
        Vector3[][] live = Shown();
        Assert.True(Distance(live, swing, movingOnly: true) > ClothSurfaceFile.RoundTripTolerance);

        Step(0, null);
        float demotionStart = AssertClose(live, Shown(), movingOnly: true);
        for (int i = 0; i < 4; i++) Step(options.MaxStepSeconds, null);
        ReadOnlyMemory<ModelVertex> warmVertices;
        long warmBytes;
        using (AllocationRegion.Start("warm detailed cloth handoff"))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            motion.Step(rig.Skeleton, pose, model, options.MaxStepSeconds, MotionLevel.Simulated);
            bool hasSample = outfit.TryGetClothPresentation(worn, motion, ClothPartId, out var warm);
            warmVertices = hasSample ? warm.DetailedVertices(0) : default;
            warmBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
        Assert.Equal(WornClothFallback.None, motion.ClothStatus.Fallback);
        Assert.False(warmVertices.IsEmpty);
        Assert.Equal(0, warmBytes);
        Vector3[][] turning = Shown();
        Assert.True(Distance(live, turning, movingOnly: true) > ClothSurfaceFile.RoundTripTolerance);
        Step(0, selection);
        float reversal = AssertClose(turning, Shown(), movingOnly: true);

        Step(0, null);
        Vector3[][] residual = ReadResidual();
        float carriedDetail = 0;
        foreach (Vector3[] material in residual)
            foreach (Vector3 vertex in material) carriedDetail = MathF.Max(carriedDetail, vertex.Length());
        Assert.True(carriedDetail > ClothSurfaceFile.RoundTripTolerance);

        // The next direction change also moves the host and wearer. Rebase the detail on the current material.
        pose[1].Translation += new Vector3(.003f, .001f, 0);
        model.Translation += new Vector3(.002f, 0, .002f);
        Step(0, selection);
        float movingReversal = AssertClose(residual, ReadResidual(), movingOnly: true);
        float host = AssertClose(Cheap(motion), Shown(), fixedOnly: true);

        Step(0, null);
        Step(options.MaxStepSeconds, null);
        for (int i = 1; i < 9; i++) Step(options.MaxStepSeconds, null);
        Step(options.MaxStepSeconds - .000001f, null);
        Vector3[][] outgoing = Shown();
        float approach = AssertClose(Cheap(motion), outgoing, movingOnly: true);
        Step(.000002f, null);
        Assert.False(outfit.TryGetClothPresentation(worn, motion, ClothPartId, out _));
        float demotionEnd = AssertClose(Cheap(motion), outgoing, movingOnly: true);
        if (Environment.GetEnvironmentVariable("KE_CLOTH_WEARER_TRACE") is { Length: > 0 } trace)
            File.WriteAllText(trace + (reuseMaterialFactor ? ".reuse.handoff.json" : ".handoff.json"), JsonSerializer.Serialize(new
            {
                cheapTravel, swingDisplacement, promotion, demotionStart, reversal, movingReversal, carriedDetail, host,
                approach, demotionEnd, warmBytes,
            }));

        void Step(float seconds, WornClothSelection? request)
        {
            motion.Step(rig.Skeleton, pose, model, seconds, MotionLevel.Simulated, request);
            Assert.Equal(WornClothFallback.None, motion.ClothStatus.Fallback);
        }

        Vector3[][] Shown()
        {
            Assert.True(outfit.TryGetClothPresentation(worn, motion, ClothPartId, out var presentation));
            Assert.True(presentation.IsCurrentPose);
            Assert.Equal(model, presentation.Model);
            var points = new Vector3[surface.Materials.Length][];
            for (int material = 0; material < points.Length; material++)
            {
                var vertices = presentation.DetailedVertices(material).Span;
                Assert.Equal(surface.Materials[material].Vertices.Length, vertices.Length);
                points[material] = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                    points[material][i] = vertices[i].Position;
            }
            return points;
        }

        Vector3[][] ReadResidual()
        {
            Assert.True(outfit.TryGetClothPresentation(worn, motion, ClothPartId, out var displayed));
            var reconstructor = new ClothDetailedReconstruction(surface);
            Assert.True(reconstructor.Material(surface, displayed.Nodes, displayed.RelativeModel, displayed.GarmentPalette));
            Vector3[][] output = Shown();
            for (int material = 0; material < output.Length; material++)
            for (int vertex = 0; vertex < output[material].Length; vertex++)
                output[material][vertex] -= reconstructor.Vertices(material)[vertex].Position;
            return output;
        }

        Vector3[][] Cheap(WornMotion? chainMotion)
        {
            int count = chainMotion is null ? outfit.WriteDraws(worn, rig.Skeleton, pose, model, rig.Key, template, draw) :
                outfit.WriteDraws(worn, chainMotion, rig.Skeleton, pose, model, rig.Key, template, draw);
            Assert.Equal(draw.Length, count);
            var points = new Vector3[surface.Materials.Length][];
            foreach (var descriptor in draw)
            {
                int material = descriptor.MaterialIndex;
                SkinnedGltfMesh mesh = rig.Scene.SkinnedSourceSnapshotForTests(descriptor.Skinned.Mesh);
                var composed = new Matrix4x4[mesh.InverseBind.Length];
                for (int bone = 0; bone < composed.Length; bone++)
                    composed[bone] = SkinningMath.Compose(descriptor.BoneMatrices.Span[bone], mesh.InverseBind[bone]);
                points[material] = new Vector3[mesh.Vertices.Length];
                for (int i = 0; i < mesh.Vertices.Length; i++)
                    points[material][i] = SkinningMath.SkinVertex(mesh.Vertices[i], composed).Position;
            }
            return points;
        }

        float AssertClose(Vector3[][] expected, Vector3[][] actual, bool movingOnly = false, bool fixedOnly = false)
        {
            float distance = Distance(expected, actual, movingOnly, fixedOnly);
            Assert.InRange(distance, 0, ClothSurfaceFile.RoundTripTolerance);
            return distance;
        }

        float Distance(Vector3[][] expected, Vector3[][] actual, bool movingOnly = false, bool fixedOnly = false)
        {
            float maximum = 0;
            int count = 0;
            for (int material = 0; material < surface.Materials.Length; material++)
            for (int i = 0; i < actual[material].Length; i++)
            {
                bool moves = surface.Materials[material].Moves(i);
                if (movingOnly && !moves || fixedOnly && moves) continue;
                count++;
                maximum = MathF.Max(maximum, Vector3.Distance(expected[material][i], actual[material][i]));
            }
            Assert.True(count > 0);
            return maximum;
        }
    }
}
