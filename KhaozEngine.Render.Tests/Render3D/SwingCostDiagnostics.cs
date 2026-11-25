using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
[Trait("Category", "Diagnostic")]
public sealed class SwingCostDiagnostics(ITestOutputHelper output)
{
    const int WearerCount = 32;
    const int WarmFrames = 120;
    const int MeasuredFrames = 600;
    const string SmokeFramesVariable = "KE_SWING_COST_SMOKE_FRAMES";
    const int HostBones = 2;
    const int CloakChains = 5, CloakJoints = 5;
    const int StrapChains = 4, StrapJoints = 3;
    const int BodyColliders = 12;

    [Fact]
    public void Report_swing_preparation_cost()
    {
        (int warmFrames, int measuredFrames) = FrameCounts();
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(new RegionRun(0, 4));
        SwingCostPiece cloak = LoadPiece(rig, "cloak", AttachmentLayer.Outerwear, "hip",
            CloakChains, CloakJoints, new Vector3(-.24f, 0, -.17f), linkStretch: .25f);
        SwingCostPiece straps = LoadPiece(rig, "straps", AttachmentLayer.Garment, "knee",
            StrapChains, StrapJoints, new Vector3(-.18f, 0, .16f));
        LoadedBodyColliders colliders = LoadColliders(rig);
        var (measurements, heldMeasurement) = MeasureOutfit(rig, body, colliders, [cloak, straps],
            WearerCount, warmFrames, measuredFrames, includeHeldSample: true);
        var (layeredMeasurements, _) = MeasureOutfit(rig, body, colliders, LayeredSwingCostOutfit.Load(rig),
            LayeredSwingCostOutfit.SimulatedWearers, warmFrames, measuredFrames, includeHeldSample: false);

        string json = JsonSerializer.Serialize(new
        {
            diagnostic = nameof(SwingCostDiagnostics),
            wearerCount = WearerCount,
            warmFrames,
            measuredFrames,
            environment = new
            {
                processorCount = Environment.ProcessorCount,
                os = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription,
            },
            measurements,
            heldMeasurement,
            layeredSimulatedWearerCount = LayeredSwingCostOutfit.SimulatedWearers,
            layeredHeldWearerCount = WearerCount - LayeredSwingCostOutfit.SimulatedWearers,
            layeredMeasurements,
        }, JsonSerializerOptions.Web);
        output.WriteLine(json);
    }

    static (Measurement[] Measurements, Measurement? HeldMeasurement) MeasureOutfit(OutfitRig rig,
        LoadedBodyRegions body, LoadedBodyColliders colliders, SwingCostPiece[] pieces, int simulatedWearers,
        int warmFrames, int measuredFrames, bool includeHeldSample)
    {
        var wearers = new Wearer[WearerCount];
        var options = new ChainSimulationOptions();
        try
        {
            for (int index = 0; index < wearers.Length; index++)
                wearers[index] = new Wearer(rig, body, pieces, colliders, options, index);

            const int fastestFrameRate = 144;
            ReadOnlySpan<Wearer> simulated = wearers.AsSpan(0, simulatedWearers);
            // Frame zero resets motion. A very short smoke sample can finish warming before the first grid step.
            if ((warmFrames - 1) * (1f / fastestFrameRate) < options.MaxStepSeconds)
                PrimeSolver(simulated, options.MaxStepSeconds);

            Measurement[] measurements =
            [
                Measure(wearers, fastestFrameRate, warmFrames, measuredFrames, simulatedWearers),
                Measure(wearers, 60, warmFrames, measuredFrames, simulatedWearers),
            ];
            Measurement? heldMeasurement = includeHeldSample ?
                Measure(wearers, fastestFrameRate, warmFrames, measuredFrames, 0) : null;
            for (int index = 0; index < measurements.Length; index++)
            {
                Measurement sample = measurements[index];
                measurements[index] = sample with
                {
                    SegmentAxisQueriesPerSimulatedWearerStep =
                        CountQueries(simulated, options, 1f / sample.FrameRateHz, warmFrames, measuredFrames),
                };
            }
            return (measurements, heldMeasurement);
        }
        finally
        {
            foreach (Wearer wearer in wearers) wearer?.Dispose();
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

    static void PrimeSolver(ReadOnlySpan<Wearer> wearers, float stepTime)
    {
        foreach (Wearer wearer in wearers)
        {
            wearer.Reset(MotionLevel.Simulated);
            wearer.Pose(0);
            wearer.Step(0);
            wearer.Pose(stepTime);
            wearer.Step(stepTime);
            wearer.WriteDraws();
        }
    }

    static Measurement Measure(Wearer[] wearers, int frameRate, int warmFrames, int measuredFrames, int simulatedWearers)
    {
        float frameTime = 1f / frameRate;
        for (int index = 0; index < wearers.Length; index++)
            wearers[index].Reset(index < simulatedWearers ? MotionLevel.Simulated : MotionLevel.Held);
        for (int frame = 0; frame < warmFrames; frame++)
        {
            foreach (Wearer wearer in wearers) wearer.Pose(frame * frameTime);
            foreach (Wearer wearer in wearers)
            {
                wearer.Step(frameTime);
                wearer.WriteDraws();
            }
        }
        foreach (Wearer wearer in wearers) wearer.ValidateDraws();

        bool measureTiming = measuredFrames == MeasuredFrames;
        var ticks = measureTiming ? new long[measuredFrames] : [];
        long allocated = 0, stepTicks = 0, drawTicks = 0;
        using (AllocationRegion.Start("swing preparation frames"))
        {
            for (int frame = 0; frame < measuredFrames; frame++)
            {
                foreach (Wearer wearer in wearers) wearer.Pose((warmFrames + frame) * frameTime);

                long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
                long start = measureTiming ? Stopwatch.GetTimestamp() : 0;
                foreach (Wearer wearer in wearers)
                {
                    long beforeStep = measureTiming ? Stopwatch.GetTimestamp() : 0;
                    wearer.Step(frameTime);
                    long afterStep = measureTiming ? Stopwatch.GetTimestamp() : 0;
                    wearer.WriteDraws();
                    long afterDraws = measureTiming ? Stopwatch.GetTimestamp() : 0;
                    stepTicks += afterStep - beforeStep;
                    drawTicks += afterDraws - afterStep;
                }
                long end = measureTiming ? Stopwatch.GetTimestamp() : 0;
                allocated += GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
                if (measureTiming)
                    ticks[frame] = end - start;
            }
        }
        foreach (Wearer wearer in wearers) wearer.ValidateDraws();

        if (!measureTiming)
            return new Measurement(frameRate, null, null, null, null, allocated, null, null);

        Array.Sort(ticks);
        double msPerTick = 1000d / Stopwatch.Frequency;
        return new Measurement(frameRate, ticks.Average() * msPerTick, Percentile(ticks, .50) * msPerTick,
            Percentile(ticks, .95) * msPerTick, ticks[^1] * msPerTick, allocated,
            stepTicks * msPerTick / measuredFrames, drawTicks * msPerTick / measuredFrames);
    }

    static double? CountQueries(ReadOnlySpan<Wearer> wearers, ChainSimulationOptions options,
        float frameTime, int warmFrames, int measuredFrames)
    {
        // Replay only the simulated wearers. Their clocks start together and the first frame resets to rest.
        // The circling walk has no gaps or teleports, so all wearers take the same fixed substeps after frame zero.
        foreach (Wearer wearer in wearers) wearer.Reset(MotionLevel.Simulated);
        var clock = new ChainStepClock(options.MaxStepSeconds, options.MaxStepsPerFrame);
        for (int frame = 0; frame < warmFrames; frame++)
        {
            if (frame > 0) clock.Advance(frameTime, out _);
            foreach (Wearer wearer in wearers)
            {
                wearer.Pose(frame * frameTime);
                wearer.Step(frameTime);
            }
        }

        var previousCounts = ChainContactCounts.Active;
        var counts = new ChainContactCounts();
        ChainContactCounts.Active = counts;
        try
        {
            int steps = 0;
            for (int frame = 0; frame < measuredFrames; frame++)
            {
                steps += clock.Advance(frameTime, out _);
                foreach (Wearer wearer in wearers)
                {
                    wearer.Pose((warmFrames + frame) * frameTime);
                    wearer.Step(frameTime);
                }
            }
            double samples = (double)steps * wearers.Length;
            return samples == 0 ? null : counts.SegmentAxis / samples;
        }
        finally
        {
            ChainContactCounts.Active = previousCounts;
        }
    }

    static long Percentile(long[] sorted, double rank) => sorted[(int)Math.Ceiling(rank * sorted.Length) - 1];

    sealed record Measurement(int FrameRateHz, double? MeanMs, double? P50Ms, double? P95Ms, double? MaxMs,
        long ManagedBytesAllocated, double? StepMeanMs, double? WriteDrawsMeanMs)
    {
        public double? SegmentAxisQueriesPerSimulatedWearerStep { get; init; }
    }

    internal static SwingCostPiece LoadPiece(OutfitRig rig, string name, AttachmentLayer layer, string anchor,
        int chainCount, int chainLength, Vector3 firstRoot, float? linkStretch = null, ColliderDefinition[]? colliders = null)
    {
        var glb = new SkinnedAttachmentGlbFixture(HostBones + chainCount * chainLength);
        Vector3 hip = ModelBindPosition(rig, "hip");
        Vector3 knee = ModelBindPosition(rig, "knee");
        bool onHip = anchor == "hip";
        var hipChildren = new JsonArray(1);
        JsonArray anchorChildren = onHip ? hipChildren : new JsonArray();
        glb.Nodes[0]!["children"] = hipChildren;
        if (!onHip) glb.Nodes[1]!["children"] = anchorChildren;
        glb.Nodes[0]!["name"] = "hip";
        glb.Nodes[1]!["name"] = "knee";
        glb.Nodes[0]!["translation"] = Translation(hip);
        glb.Nodes[1]!["translation"] = Translation(knee - hip);
        glb.SetInverseBind(0, rig.Body.InverseBind[rig.Map.Bone("hip")]);
        glb.SetInverseBind(1, rig.Body.InverseBind[rig.Map.Bone("knee")]);

        var chains = new ChainDefinition[chainCount];
        var links = new ChainLink[linkStretch.HasValue ? chainCount - 1 : 0];
        var settings = new ChainSettings(8f, 3f, 1f, .85f, MathF.PI * .65f, .02f);
        var segment = new Vector3(0, -.18f, 0);
        Vector3 anchorPosition = onHip ? hip : knee;
        for (int chain = 0; chain < chains.Length; chain++)
        {
            int first = HostBones + chain * chainLength;
            anchorChildren.Add(first);
            var joints = new string[chainLength];
            Vector3 root = firstRoot + Vector3.UnitX * (.12f * chain);
            for (int joint = 0; joint < joints.Length; joint++)
            {
                int node = first + joint;
                joints[joint] = $"{name}-{chain}-{joint}";
                glb.Nodes[node]!["name"] = joints[joint];
                glb.Nodes[node]!["translation"] = Translation(joint == 0 ? root : segment);
                if (joint + 1 < joints.Length) glb.Nodes[node]!["children"] = new JsonArray(node + 1);
                glb.SetInverseBind(node, Matrix4x4.CreateTranslation(-(anchorPosition + root + joint * segment)));
            }
            chains[chain] = new ChainDefinition(joints, segment, settings);
        }
        for (int link = 0; link < links.Length; link++) links[link] = new ChainLink(link, link + 1);

        // The shared triangle needs only three representative weights. The solver steps every declared joint.
        for (int vertex = 0; vertex < 3; vertex++)
        {
            int chain = vertex * (chainCount - 1) / 2;
            int lastJoint = HostBones + (chain + 1) * chainLength - 1;
            glb.SetElement(1, vertex, lastJoint, 0, 0, 0);
            glb.SetElement(2, vertex, 1, 0, 0, 0);
        }

        var part = new SkinnedAttachmentPartDefinition(1, rig.Source.Mesh(name + ".glb", glb.Build()))
        {
            Regions = [new NamedRegionRun("alpha", 1)],
            Motion = new ChainMotionDefinition(chains, links) { LinkStretch = linkStretch },
        };
        var loaded = rig.Load(name + ".json", new AttachmentSetDefinition([part], layer, [], colliders ?? []));
        return new SwingCostPiece(loaded, HostBones + chainCount * chainLength);
    }

    static JsonArray Translation(Vector3 position) => new(position.X, position.Y, position.Z);

    // OutfitRig's synthetic inverse binds are pure translations.
    internal static Vector3 ModelBindPosition(OutfitRig rig, string joint) =>
        -rig.Body.InverseBind[rig.Map.Bone(joint)].Translation;

    static LoadedBodyColliders LoadColliders(OutfitRig rig)
    {
        const string key = "body-colliders.json";
        var capsules = new ColliderDefinition[BodyColliders];
        for (int index = 0; index < capsules.Length; index++)
        {
            string joint = index < BodyColliders / 2 ? "hip" : "knee";
            int offset = index % (BodyColliders / 2);
            Vector3 center = ModelBindPosition(rig, joint) + new Vector3((offset % 3 - 1) * .18f, -.16f,
                offset < 3 ? -.08f : .08f);
            Vector3 halfAxis = Vector3.UnitY * .14f;
            capsules[index] = new ColliderDefinition(joint, center - halfAxis, center + halfAxis, .10f);
        }
        var body = new BodyColliderSet(OutfitRig.Rig, capsules);
        return rig.Cache.GetBodyColliders(key, rig.Source.Add(key, BodyCollidersJson.Write(body)), OutfitRig.Rig, rig.Body);
    }

    sealed class Wearer : IDisposable
    {
        static readonly RigidInstanceDraw Template = new(default, Matrix4x4.Identity);
        readonly BoundOutfit outfit;
        readonly WornMotion motion;
        readonly Skeleton skeleton;
        readonly JointPose[] locals;
        readonly Matrix4x4[] palette;
        readonly int hip, knee;
        readonly float phase;
        readonly MotionKey key;
        readonly SwingCostPiece[] pieces;
        readonly AttachmentDraw[][] draws;
        MotionLevel level;
        Matrix4x4 model;

        internal Wearer(OutfitRig rig, LoadedBodyRegions body, SwingCostPiece[] pieces,
            LoadedBodyColliders colliders, ChainSimulationOptions options, int index)
        {
            skeleton = rig.Skeleton;
            locals = (JointPose[])skeleton.RestLocal.Clone();
            palette = new Matrix4x4[skeleton.BoneCount];
            hip = skeleton.IndexOf("hip");
            knee = skeleton.IndexOf("knee");
            phase = MathF.Tau * index / WearerCount;
            key = MotionKey.From((ulong)(index + 1));
            this.pieces = pieces;
            draws = pieces.Select(_ => new AttachmentDraw[1]).ToArray();

            // Each wearer has independent draw scratch as well as independent motion.
            outfit = rig.Cache.BindOutfit(body, pieces.Select(piece => rig.Bind(piece.Set)).ToArray());
            try
            {
                for (int worn = 0; worn < draws.Length; worn++) Assert.Equal(draws[worn].Length, outfit.DrawCount(worn));
                motion = outfit.CreateMotion(colliders, options);
            }
            catch
            {
                outfit.Dispose();
                throw;
            }
        }

        internal void Reset(MotionLevel level)
        {
            this.level = level;
            motion.Reset();
        }

        internal void Pose(float seconds)
        {
            float stride = MathF.Tau * 1.8f * seconds + phase;
            locals[hip].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .16f * MathF.Sin(stride));
            locals[knee].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .50f * MathF.Sin(stride + .8f));
            skeleton.ComposeInto(locals, palette);

            // A circle with constant tangential speed of 3 m/s, with local +Z facing along the tangent.
            const float turnRate = .65f;
            const float radius = 3f / turnRate;
            float turn = phase + turnRate * seconds;
            var position = new Vector3(radius * MathF.Cos(turn), 0, radius * MathF.Sin(turn));
            model = Matrix4x4.CreateRotationY(-turn) * Matrix4x4.CreateTranslation(position);
        }

        internal void Step(float frameTime) => motion.Step(skeleton, palette, model, frameTime, level);

        internal void WriteDraws()
        {
            for (int worn = 0; worn < draws.Length; worn++)
                outfit.WriteDraws(worn, motion, skeleton, palette, model, key, Template, draws[worn]);
        }

        internal void ValidateDraws()
        {
            for (int worn = 0; worn < draws.Length; worn++)
            {
                AttachmentDraw draw = draws[worn][0];
                Assert.Equal(1u, draw.PartId);
                Assert.Equal(pieces[worn].PaletteBones.HasValue, draw.IsSkinned);
                if (pieces[worn].PaletteBones is int joints)
                {
                    ValidateMatrix(draw.Skinned.Model);
                    ReadOnlySpan<Matrix4x4> palette = draw.BoneMatrices.Span;
                    Assert.Equal(joints, palette.Length);
                    foreach (Matrix4x4 pose in palette) ValidateMatrix(pose);
                }
                else
                {
                    ValidateMatrix(draw.Rigid.World);
                }
            }
        }

        static void ValidateMatrix(in Matrix4x4 matrix)
        {
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    Assert.True(float.IsFinite(matrix[row, column]), "A draw matrix is non-finite.");
        }

        public void Dispose() => outfit.Dispose();
    }
}
