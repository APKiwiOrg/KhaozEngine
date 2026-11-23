using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
[Trait("Category", "Diagnostic")]
public sealed partial class LiftBindCostDiagnostics(ITestOutputHelper output)
{
    const int WearerCount = 32;
    const int WarmFrames = 120;
    const int MeasuredFrames = 600;
    const string SmokeFramesVariable = "KE_LIFT_COST_SMOKE_FRAMES";
    const int PanelSide = 12;
    const int PanelMaterialCount = 2;
    const float Bearing = .00390625f;
    const float Clearance = .001953125f;

    [Fact]
    public void Report_lift_binding_and_draw_cost()
    {
        (int warmFrames, int measuredFrames) = FrameCounts();
        using var rig = new OutfitRig();
        LoadedBodyRegions body = OutfitLiftDrawTests.Body(rig);
        LoadedAttachmentSet armour = LoadArmour(rig);
        AttachmentMeshRef garmentMesh = rig.Garment("liner.glb");
        var inputs = new BoundAttachmentSet[WearerCount][];
        for (int wearer = 0; wearer < inputs.Length; wearer++)
        {
            // Share the source meshes but give every wearer a different displacement.
            float standoff = .015625f + wearer / 4096f;
            BoundAttachmentSet garment = rig.Wear($"liner-{wearer}.json", new AttachmentSetDefinition(
                [new SkinnedAttachmentPartDefinition(1, garmentMesh) { Regions = [new("alpha", 4)] }],
                AttachmentLayer.Garment, [], [], [new("alpha", standoff)]));
            inputs[wearer] = [garment, rig.Bind(armour)];
        }

        var lifted = new BoundOutfit[WearerCount];
        var bare = new BoundOutfit[WearerCount];
        try
        {
            Cost liftedBinds = MeasureBinds(rig.Cache, body, inputs, lifted);
            for (int wearer = 0; wearer < bare.Length; wearer++)
                bare[wearer] = rig.Cache.BindOutfit(body, [rig.Bind(armour)]);

            ArmourDraws[] liftedDraws = PrepareArmourDraws(rig, lifted, wornIndex: 1);
            ArmourDraws[] bareDraws = PrepareArmourDraws(rig, bare, wornIndex: 0);
            Cost liftedArmourDrawFrames = MeasureDraws(rig, liftedDraws, warmFrames, measuredFrames);
            Cost bareArmourDrawFrames = MeasureDraws(rig, bareDraws, warmFrames, measuredFrames);
            ValidateViews(rig.Scene, liftedDraws, bareDraws);
            Cost footprintBinds = MeasureFootprintBinds(rig, out int footprintBodyTriangleCount);

            output.WriteLine(JsonSerializer.Serialize(new
            {
                diagnostic = nameof(LiftBindCostDiagnostics),
                wearerCount = WearerCount,
                warmFrames,
                measuredFrames,
                environment = new
                {
                    processorCount = Environment.ProcessorCount,
                    os = RuntimeInformation.OSDescription,
                    runtime = RuntimeInformation.FrameworkDescription,
                },
                liftedBinds,
                liftedArmourDrawFrames,
                bareArmourDrawFrames,
                footprintBodyTriangleCount,
                footprintBinds,
            }, JsonSerializerOptions.Web));
        }
        finally
        {
            foreach (BoundOutfit outfit in bare) outfit?.Dispose();
            foreach (BoundOutfit outfit in lifted) outfit?.Dispose();
        }
    }

    static (int Warm, int Measured) FrameCounts()
    {
        string? value = Environment.GetEnvironmentVariable(SmokeFramesVariable);
        if (value is null) return (WarmFrames, MeasuredFrames);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
            count < 1 || count >= WarmFrames)
            throw new ArgumentException($"{SmokeFramesVariable} must be a frame count from 1 through {WarmFrames - 1}.");
        return (count, count);
    }

    static Cost MeasureBinds(AttachmentSetCache cache, LoadedBodyRegions body, BoundAttachmentSet[][] inputs,
        BoundOutfit[] outfits)
    {
        var ticks = new long[outfits.Length];
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int wearer = 0; wearer < outfits.Length; wearer++)
        {
            BoundAttachmentSet[] worn = inputs[wearer];
            long start = Stopwatch.GetTimestamp();
            BoundOutfit outfit = cache.BindOutfit(body, worn);
            ticks[wearer] = Stopwatch.GetTimestamp() - start;
            outfits[wearer] = outfit;
        }
        return Summarize(ticks, GC.GetAllocatedBytesForCurrentThread() - bytesBefore);
    }

    static ArmourDraws[] PrepareArmourDraws(OutfitRig rig, BoundOutfit[] outfits, int wornIndex)
    {
        var wearers = new ArmourDraws[outfits.Length];
        for (int wearer = 0; wearer < wearers.Length; wearer++)
        {
            BoundOutfit outfit = outfits[wearer];
            int drawCount = outfit.DrawCount(wornIndex);
            Assert.Equal(PanelMaterialCount + 1, drawCount);
            WornMotion motion = outfit.CreateMotion(null);
            motion.Step(rig.Skeleton, rig.Palette, rig.Model, 1f / 60f);
            var draws = new ArmourDraws(outfit, wornIndex, MotionKey.From((ulong)wearer + 1), motion, drawCount);
            Assert.Equal(drawCount, draws.Write(rig));
            wearers[wearer] = draws;
        }
        return wearers;
    }

    static Cost MeasureDraws(OutfitRig rig, ArmourDraws[] wearers, int warmFrames, int measuredFrames)
    {
        for (int frame = 0; frame < warmFrames; frame++)
            foreach (ArmourDraws wearer in wearers) wearer.Write(rig);

        var ticks = new long[measuredFrames];
        long allocated;
        using (AllocationRegion.Start("lift armour draw frames"))
        {
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < ticks.Length; frame++)
            {
                long start = Stopwatch.GetTimestamp();
                foreach (ArmourDraws wearer in wearers) wearer.Write(rig);
                ticks[frame] = Stopwatch.GetTimestamp() - start;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        }
        Assert.Equal(0, allocated);
        return Summarize(ticks, allocated);
    }

    static void ValidateViews(Scene3D scene, ArmourDraws[] lifted, ArmourDraws[] bare)
    {
        var usedViews = new HashSet<SkinnedMeshHandle>();
        for (int wearer = 0; wearer < lifted.Length; wearer++)
        {
            AttachmentDraw[] liftedDraws = lifted[wearer].Draws;
            AttachmentDraw[] bareDraws = bare[wearer].Draws;
            for (int draw = 0; draw < liftedDraws.Length; draw++)
            {
                SkinnedMeshHandle view = liftedDraws[draw].Skinned.Mesh;
                SkinnedMeshHandle source = bareDraws[draw].Skinned.Mesh;
                Assert.True(scene.IsSkinnedMeshView(view));
                Assert.False(scene.IsSkinnedMeshView(source));
                Assert.True(usedViews.Add(view), "Each wearer must create its own displaced armour views.");
                Assert.NotEqual(scene.SkinnedSourceSnapshotForTests(source).Vertices[0].Position,
                    scene.SkinnedSourceSnapshotForTests(view).Vertices[0].Position);
            }
        }
    }

    static Cost Summarize(long[] ticks, long allocated)
    {
        Array.Sort(ticks);
        double msPerTick = 1000d / Stopwatch.Frequency;
        return new Cost(ticks.Average() * msPerTick, Percentile(ticks, .50) * msPerTick,
            Percentile(ticks, .95) * msPerTick, ticks[^1] * msPerTick, ticks.Sum() * msPerTick, allocated);
    }

    static long Percentile(long[] sorted, double rank) => sorted[(int)Math.Ceiling(rank * sorted.Length) - 1];

    static LoadedAttachmentSet LoadArmour(OutfitRig rig)
    {
        SkinnedAttachmentGlbFixture panel = OutfitRig.GarmentFixture();
        ushort[] triangles = GridTriangles(PanelSide, PanelSide);
        int vertexCount = PanelSide * PanelSide;
        panel.SetUniformSkinning(0, vertexCount, new Vector4(0, 1, 0, 0), new Vector4(.5f, .5f, 0, 0));
        panel.SetIndexedMaterialParts(triangles, PanelMaterialCount);
        var directions = new Vector3[vertexCount];
        var bearings = new float[vertexCount];
        Array.Fill(directions, Vector3.UnitZ);
        Array.Fill(bearings, Bearing);
        for (int material = 0; material < PanelMaterialCount; material++)
        {
            var positions = new Vector3[vertexCount];
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                int column = vertex % PanelSide + material * (PanelSide - 1);
                int row = vertex / PanelSide;
                positions[vertex] = new Vector3(column / 32f, 1f + row / 32f, Bearing);
            }
            panel.SetPositions(material, positions);
            panel.SetLiftAttributes(material, directions, bearings);
        }
        var shell = new SkinnedAttachmentPartDefinition(1, rig.Source.Mesh("shell.glb", panel.Build()))
        {
            Regions = [new("alpha", PanelMaterialCount * triangles.Length / 3)],
            Lift = new(Clearance),
        };

        SkinnedAttachmentGlbFixture chain = SkinnedAttachmentGlbFixture.Chained();
        chain.SetLiftAttributes(0, [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Bearing, Bearing, Bearing]);
        var chainPart = (SkinnedAttachmentPartDefinition)SkinnedAttachmentBindingTests
            .ChainSet(rig.Source.Mesh("chain.glb", chain.Build())).Parts[0];
        return rig.Load("armour.json",
            new AttachmentSetDefinition([shell, chainPart with { Lift = new(Clearance) }], AttachmentLayer.Armour, []));
    }

    sealed record Cost(double MeanMs, double P50Ms, double P95Ms, double MaxMs, double TotalMs, long ManagedBytesAllocated);

    sealed class ArmourDraws(BoundOutfit outfit, int wornIndex, MotionKey key, WornMotion motion, int drawCount)
    {
        static readonly RigidInstanceDraw Template = new(default, Matrix4x4.Identity);
        internal AttachmentDraw[] Draws { get; } = new AttachmentDraw[drawCount];

        internal int Write(OutfitRig rig) =>
            outfit.WriteDraws(wornIndex, motion, rig.Skeleton, rig.Palette, rig.Model, key, Template, Draws);
    }
}
