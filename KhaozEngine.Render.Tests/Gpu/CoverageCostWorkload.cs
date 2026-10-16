using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// N actors of one cost fixture in a caller-owned scene, driven the way a game drives equipped characters. Coverage
/// on prepares each actor through the public <see cref="HostCoverageBinding"/> and submits the covered host and its
/// frozen equipment. Coverage off draws the same hosts and plates on the scene's ordinary skinning route. A declined
/// preparation is counted and drawn on the uncovered current pose, never dropped. The caller encodes and submits
/// between <see cref="Reserve"/> and <see cref="Collect"/>.
/// <para>Timing reads only the injected clock. The stage, resolution and surface observers stay installed for the
/// workload's lifetime, so their clock reads are part of the measured path and the report says so.</para>
/// </summary>
internal sealed class CoverageCostWorkload : IDisposable
{
    // The point light sits 2 units over actor 0 with radius 3, inside the accepted native coverage proof's envelope
    // (radius 6, faces at distance 4). Wider point projections refuse by design until #1425 is resolved, so they
    // are outside this workload rather than hidden inside its fallback counts.
    internal const float ActorSpacingX = 3, ActorSpacingY = 1.5f, PointRadius = 3;
    readonly Scene3D scene;
    readonly CoverageCostGeometry geometry;
    readonly Func<long> clock;
    readonly bool coverage, full;
    readonly AttachmentSetCache cache;
    readonly Scene3DAttachmentScene adapter;
    readonly Skeleton skeleton;
    readonly SkinnedMeshHandle[] hosts;
    readonly BoundAttachmentSet set;
    readonly HostCoverageBinding[] bindings;
    readonly SkinnedCoverageFrame[] frames;
    readonly bool[] accepted;
    readonly Matrix4x4[] models;
    readonly Matrix4x4[] palette = new Matrix4x4[2];
    readonly RigidInstanceDraw[] uncovered;
    readonly RigidInstanceDraw template = new(default, Matrix4x4.Identity) { Tint = Color.White };
    readonly int[] declines = new int[Enum.GetValues<CoverageFailureCode>().Length];
    readonly int[] fallbacks = new int[Enum.GetValues<CoverageFailureCode>().Length];
    readonly Vector3 pointLight;
    readonly bool executesOnGpu;
    // Certified contexts by role over measured accepted actor frames: totals, and minimum and maximum per actor frame.
    readonly long[] roleTotals = new long[2 * CoveragePreparation.EquipmentRoleOffset];
    readonly int[] roleMin = new int[2 * CoveragePreparation.EquipmentRoleOffset], roleMax = new int[2 * CoveragePreparation.EquipmentRoleOffset];
    long roleActorFrames, pointFaceMatrices, outlineGroups, measuredFrames;
    long skinStart, skinTicks, geometryStart, geometryTicks, geometryCertificationStart, geometryCertificationTicks;
    long certificationStart, certificationTicks, ordinaryStart, ordinaryTicks, uploadStart, uploadTicks;
    long resolutionStart, resolutionTicks, resolutionBytesStart, resolutionBytes, surfaceStart, surfaceTicks, surfaceBytesStart, surfaceBytes;
    int skinned, evaluated, geometryCertified, certified, acceptedCount, declinedCount, lastCovered = -1;
    CoverageCostTopology? observed;
    // The last accepted preparation. A later refusal resets the actor's token, but this actor-owned object remains.
    CoveragePreparation? retained;

    internal CoverageCostWorkload(Scene3D scene, CoverageCostGeometry geometry, int actors, bool coverage, bool gpuSkinning,
        CoverageCostPassSet passes, Func<long> clock, bool executesOnGpu = false)
    {
        this.scene = scene; this.geometry = geometry; this.clock = clock; this.coverage = coverage; this.executesOnGpu = executesOnGpu;
        Array.Fill(roleMin, int.MaxValue);
        full = passes == CoverageCostPassSet.Full;
        int perRow = (int)Math.Ceiling(Math.Sqrt(actors));
        models = new Matrix4x4[actors];
        for (int i = 0; i < actors; i++)
            models[i] = Matrix4x4.CreateTranslation(i % perRow * ActorSpacingX, i / perRow * ActorSpacingY, 0);
        pointLight = new Vector3(0, 0, 2);
        Configure(gpuSkinning, actors, perRow);
        Layout = $"{actors} actors on a {perRow}-column grid at {ActorSpacingX} x {ActorSpacingY} world units, each a "
            + $"{CoverageCostGeometry.Columns} x {CoverageCostGeometry.Rows} cell host at spacing {CoverageCostGeometry.Spacing}, "
            + $"poses alternate lifted and bent each frame";

        skeleton = CoverageCostGeometry.CreateSkeleton();
        var meshes = geometry.HostMeshes(skeleton);
        hosts = new SkinnedMeshHandle[meshes.Length];
        for (int i = 0; i < meshes.Length; i++) hosts[i] = scene.LoadSkinnedMesh(meshes[i]);
        adapter = new Scene3DAttachmentScene(scene);
        var source = new AttachmentTestSource();
        cache = new AttachmentSetCache(adapter, source);
        var mesh = source.Mesh("cost-plates.glb", geometry.EquipmentGlb());
        string setHash = source.Add("cost-set.json", AttachmentSetJson.Write(new AttachmentSetDefinition(
            [new RigidAttachmentPartDefinition(1, mesh, "root", Matrix4x4.Identity)])));
        var loaded = cache.Get("cost-set.json", setHash);
        string coverageHash = source.Add("cost-coverage.json", EquipmentCoverageJson.Write(new EquipmentCoverageDefinition(setHash,
            HostCoverageMapJson.SemanticSha256(geometry.Map), geometry.Commands)));
        var equipment = cache.GetCoverage(loaded, "cost-coverage.json", coverageHash);
        set = loaded.Bind(new ContractJointMap(skeleton, new SkeletonContract([new("root", null, true), new("hip", "root", true)])));
        uncovered = new RigidInstanceDraw[set.DrawCount];
        frames = new SkinnedCoverageFrame[actors];
        accepted = new bool[actors];
        bindings = new HostCoverageBinding[coverage ? actors : 0];
        for (int i = 0; i < bindings.Length; i++)
        {
            bindings[i] = new HostCoverageBinding(adapter, skeleton, hosts, Actor(i), CoverageFallback.UncoveredCurrentPose);
            if (!bindings[i].TrySetEquipment([new CoveredAttachmentInstance(1000 + (ulong)i, set, equipment)], out var failure))
                throw new InvalidOperationException($"Cost fixture equipment was refused: {failure}");
        }
        scene.CoverageStageObserverForTests = Stage;
        scene.CoverageAllocationObserverForTests = Resolution;
        scene.CoverageSurfaceAllocationObserverForTests = Surface;
    }

    internal string Camera { get; private set; } = "";
    internal string Light { get; private set; } = "";
    internal string Layout { get; }
    /// <summary>Observed from the first accepted conformer. The off route has no conformer, so it reports the same
    /// public <see cref="CoverageRefinement.Build"/> output the conformer would compile.</summary>
    internal CoverageCostTopology Topology => observed ?? geometry.Topology;
    /// <summary>The first refused preparation, kept whole so a report can name its counts and limits.</summary>
    internal CoverageFailure? FirstDecline { get; private set; }
    /// <summary>The first render-time fallback, kept whole for the same reason.</summary>
    internal CoverageFailure? FirstFallback { get; private set; }
    /// <summary>Test seam: a palette that replaces the alternating poses, null for the ordinary poses.</summary>
    internal Matrix4x4[]? PoseOverride { get; set; }
    /// <summary>Test seam: disposes one actor's binding, so its submitted preparation resolves to the late fallback.</summary>
    internal void DisposeBindingForTests(int actor) => bindings[actor].Dispose();

    static MotionKey Actor(int index) => MotionKey.From(100 + (ulong)index);

    void Configure(bool gpuSkinning, int actors, int perRow)
    {
        scene.UseGpuSkinning = gpuSkinning;
        scene.FrustumCulling = true;
        scene.EnableTiming = true;
        scene.Post.Starfield = false;
        scene.Post.Sky.Enabled = false;
        scene.Post.Atmosphere.Enabled = false;
        scene.Post.Outline = false;
        scene.Post.Hdr.Enabled = false;
        scene.Post.Bloom.Enabled = false;
        scene.Post.LightDirection = new(.3f, -.4f, -1);
        scene.Post.Quality.Shadows.Mode = full ? ShadowMode.ShadowMap : ShadowMode.Off;
        scene.ForceTemporalForTests = full;
        int rows = (actors + perRow - 1) / perRow;
        var size = new Vector3((perRow - 1) * ActorSpacingX + CoverageCostGeometry.Columns * CoverageCostGeometry.Spacing,
            (rows - 1) * ActorSpacingY + CoverageCostGeometry.Rows * CoverageCostGeometry.Spacing, 1);
        var center = new Vector3((perRow - 1) * ActorSpacingX / 2, (rows - 1) * ActorSpacingY / 2, 0);
        scene.Camera.Frame(center, size);
        scene.Camera.NearPlane = .1f;
        Camera = $"IsoCamera3D.Frame(center {center}, size {size}), near {scene.Camera.NearPlane}, frustum culling on";
        Light = full
            ? $"directional {scene.Post.LightDirection} with cascaded shadow maps, one dynamic shadowed point light radius {PointRadius} at {pointLight} near actor 0, target outlines on every actor, temporal resolve forced"
            : $"directional {scene.Post.LightDirection}, shadows off, no point light, no outlines, temporal off";
    }

    /// <summary>Begin, then prepare and queue every actor. Preparation and queueing are timed separately.</summary>
    internal void Submit(int frame, ref CoverageCostFrameSample sample)
    {
        scene.Begin();
        skinTicks = geometryTicks = geometryCertificationTicks = certificationTicks = ordinaryTicks = uploadTicks = 0;
        resolutionTicks = resolutionBytes = surfaceTicks = surfaceBytes = 0;
        skinned = evaluated = geometryCertified = certified = acceptedCount = declinedCount = 0;
        lastCovered = -1;
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        if (PoseOverride is { } pose) pose.CopyTo(palette, 0);
        else CoverageCostGeometry.Pose(frame, palette);
        MeshOutlineGroup group = default;
        if (full)
        {
            scene.AddLight(pointLight, Color.White, PointRadius, 1, LightShadow.Dynamic);
            group = scene.BeginMeshOutline(Color.White, 1);
        }
        long prepare = 0, submission = 0;
        for (int i = 0; i < models.Length; i++)
        {
            long start = clock();
            bool ok = false;
            if (coverage)
            {
                ok = bindings[i].TryPrepare(skeleton, palette, models[i], template, out frames[i], out var failure);
                if (ok) acceptedCount++;
                else
                {
                    declinedCount++;
                    declines[(int)failure.Code]++;
                    FirstDecline ??= failure;
                }
            }
            int count = ok ? 0 : set.WriteDraws(skeleton, palette, models[i], MotionKey.Combine(Actor(i), 1), template, uncovered);
            long prepared = clock();
            prepare += prepared - start;
            accepted[i] = ok;
            foreach (var handle in hosts)
            {
                var draw = new SkinnedInstanceDraw(handle, models[i]) { Motion = Actor(i), Coverage = ok ? frames[i] : default };
                adapter.DrawSkinned(draw, palette);
                if (full) adapter.DrawSkinnedOutline(group, draw, palette);
            }
            if (ok)
                foreach (var draw in bindings[i].PreparedEquipmentDraws.Span)
                {
                    adapter.DrawMesh(draw.Rigid);
                    if (full) adapter.DrawMeshOutline(group, draw.Rigid);
                }
            else
                for (int m = 0; m < count; m++)
                {
                    adapter.DrawMesh(uncovered[m]);
                    if (full) adapter.DrawMeshOutline(group, uncovered[m]);
                }
            submission += clock() - prepared;
        }
        sample.PrepareBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        sample.PrepareTicks = prepare;
        sample.SubmissionTicks = submission;
        sample.SkinningTicks = skinTicks;
        sample.GeometryTicks = geometryTicks;
        sample.GeometryCertificationTicks = geometryCertificationTicks;
    }

    internal void Reserve(int width, int height, ref CoverageCostFrameSample sample)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = clock();
        scene.PrepareFrame(width, height);
        sample.ReserveTicks = clock() - start;
        sample.ReserveBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
    }

    /// <summary>After encoding: encode-side spans, outcomes, role evidence and engine frame counters. Evidence
    /// accumulates only for measured frames.</summary>
    internal void Collect(ref CoverageCostFrameSample sample, bool measured)
    {
        sample.ResolutionTicks = resolutionTicks;
        sample.OrdinarySkinningTicks = ordinaryTicks;
        sample.SkinnedUploadTicks = uploadTicks;
        sample.OrdinaryCpuSkinnedVertices = scene.OrdinaryCpuSkinnedVerticesForTests;
        sample.GeometryCertified = geometryCertified;
        sample.CertificationTicks = certificationTicks;
        sample.SurfaceTicks = surfaceTicks;
        sample.ResolutionBytes = resolutionBytes;
        sample.SurfaceBytes = surfaceBytes;
        sample.ActorFrames = models.Length;
        sample.Accepted = acceptedCount;
        sample.Declined = declinedCount;
        sample.Skinned = skinned;
        sample.Evaluated = evaluated;
        sample.Certified = certified;
        for (int i = 0; i < models.Length; i++)
        {
            if (!accepted[i]) continue;
            // The frozen decision is readable even after a late disposal, when the public accessors refuse the owner.
            var outcome = scene.CoverageOutcomeForTests(frames[i]);
            if (outcome.Presentation == CoveragePresentation.Covered)
            {
                sample.Covered++;
                lastCovered = i;
            }
            else
            {
                sample.Fallback++;
                fallbacks[(int)outcome.Failure.Code]++;
                FirstFallback ??= outcome.Failure;
            }
            retained = frames[i].Preparation;
            if (!scene.CoverageFrameCurrentForTests(frames[i])) continue;
            sample.Passes += scene.CoveragePassesForTests(frames[i]).Length;
            if (measured)
            {
                int[] roles = frames[i].Preparation!.PassRoles;
                for (int r = 0; r < roles.Length; r++)
                {
                    roleTotals[r] += roles[r];
                    roleMin[r] = Math.Min(roleMin[r], roles[r]);
                    roleMax[r] = Math.Max(roleMax[r], roles[r]);
                }
                roleActorFrames++;
            }
            if (scene.CoverageMainVisibleForTests(frames[i])) sample.MainVisible++;
            if (observed is null) observed = CoverageCostGeometry.Observe(frames[i].Preparation!.Owner.Topology, geometry.Commands.Length);
        }
        sample.OutlineGroups = scene.TargetOutlineGroupsRenderedForTests;
        sample.Stats = scene.LastFrameStats;
        sample.PassTimings = scene.PassTimingsMs;
        if (measured)
        {
            measuredFrames++;
            pointFaceMatrices += scene.CoveragePointMatricesForTests.Length;
            outlineGroups += sample.OutlineGroups;
        }
    }

    /// <summary>Certified contexts are what render certification captured and checked for each preparation, by
    /// role. Recorded counts are what the encoder scheduled. Neither is proof of GPU execution on the recording device.</summary>
    internal IReadOnlyList<CoverageCostMetric> ReadEvidence()
    {
        string[] roles = ["main", "motion", "outline", "cascade", "pointFace"];
        string recorded = executesOnGpu ? "recorded and submitted to the native device" : "recorded into a null command list, not executed on a GPU";
        var result = new List<CoverageCostMetric>();
        for (int owner = 0; owner < 2; owner++)
            for (int r = 0; r < roles.Length; r++)
            {
                int index = owner * CoveragePreparation.EquipmentRoleOffset + r;
                string name = $"certifiedContext.{(owner == 0 ? "host" : "equipment")}.{roles[r]}";
                if (roleActorFrames == 0)
                {
                    var status = coverage ? CoverageCostStatus.Unavailable : CoverageCostStatus.NotApplicable;
                    string reason = coverage ? "no accepted measured actor frame" : "coverage is off";
                    result.Add(CoverageCostMetric.Missing(name + ".total", "count", status, reason));
                    result.Add(CoverageCostMetric.Missing(name + ".minPerActorFrame", "count", status, reason));
                    result.Add(CoverageCostMetric.Missing(name + ".maxPerActorFrame", "count", status, reason));
                    continue;
                }
                const string Provenance = "render certification contexts tagged by role at capture over measured accepted actor frames, not GPU work";
                result.Add(CoverageCostMetric.Measured(name + ".total", "count", roleTotals[index], Provenance));
                result.Add(CoverageCostMetric.Measured(name + ".minPerActorFrame", "count", roleMin[index], Provenance));
                result.Add(CoverageCostMetric.Measured(name + ".maxPerActorFrame", "count", roleMax[index], Provenance));
            }
        result.Add(CoverageCostMetric.Measured("evidence.certifiedActorFrames", "count", roleActorFrames, "measured accepted actor frames"));
        result.Add(CoverageCostMetric.Measured("recorded.pointFaceMatrices", "count", pointFaceMatrices,
            $"point shadow face matrices scheduled over {measuredFrames} measured frames, {recorded}"));
        result.Add(CoverageCostMetric.Measured("recorded.outlineGroups", "count", outlineGroups,
            $"target outline groups rendered over {measuredFrames} measured frames, {recorded}"));
        return result;
    }

    /// <summary>Capacities after the last frame. Storage only grows, so these are the peaks the run observed.</summary>
    internal IReadOnlyList<CoverageCostMetric> ReadCapacities()
    {
        const string Peak = "read after the last frame, storage only grows";
        var result = new List<CoverageCostMetric>();
        if (retained is { } p)
        {
            // The preparation object is actor-owned and outlives its frame, so its capacities stay readable. A frame
            // token is only read below when it is current and Covered.
            var scratch = p.Owner.Scratch;
            int vertex = Unsafe.SizeOf<ModelVertex>();
            const string Retained = "from the last accepted preparation, actor-owned storage that only grows";
            result.Add(CoverageCostMetric.Measured("actor.scratch.vertices", "elements", scratch.VertexCapacity, Retained));
            result.Add(CoverageCostMetric.Measured("actor.scratch.triangles", "elements", scratch.TriangleCapacity, Retained));
            result.Add(CoverageCostMetric.Measured("actor.preparation.posed", "elements", p.Posed.Length, Retained));
            result.Add(CoverageCostMetric.Measured("actor.preparation.before", "elements", p.Before.Length, Retained));
            result.Add(CoverageCostMetric.Measured("actor.preparation.candidate", "elements", p.Candidate.Length, Retained));
            result.Add(CoverageCostMetric.Measured("actor.preparation.passes", "elements", p.Passes.Capacity, Retained));
            double surfaces = (p.Posed.Length + p.Before.Length + p.Candidate.Length + scratch.Staged.Length + scratch.Before.Length
                + scratch.After.Length) * (double)vertex;
            result.Add(CoverageCostMetric.Estimated("actor.surfaces.bytesEstimated", "bytes", surfaces,
                $"six ModelVertex arrays times {vertex} bytes, excluding object headers and smaller scratch arrays"));
            result.Add(CoverageCostMetric.Estimated("actors.surfaces.bytesEstimated", "bytes", surfaces * bindings.Length,
                "actor.surfaces.bytesEstimated times the covered actor count"));
        }
        else
        {
            var status = coverage ? CoverageCostStatus.Unavailable : CoverageCostStatus.NotApplicable;
            string reason = coverage ? "no preparation was accepted" : "coverage is off";
            result.Add(CoverageCostMetric.Missing("actor.scratch.vertices", "elements", status, reason));
            result.Add(CoverageCostMetric.Missing("actor.surfaces.bytesEstimated", "bytes", status, reason));
        }
        // The CPU skinned vertex buffer is reached through a covered slice, so only a current Covered token is read.
        if (lastCovered >= 0 && scene.CoverageFrameCurrentForTests(frames[lastCovered])
            && scene.GetCoverageOutcome(frames[lastCovered]).Presentation == CoveragePresentation.Covered)
            result.Add(CoverageCostMetric.Measured("shared.cpuSkinnedVertexBuffer.bytes", "bytes",
                scene.CoverageSliceForTests(frames[lastCovered], 0).Vertices.SizeInBytes, Peak));
        else
            result.Add(CoverageCostMetric.Missing("shared.cpuSkinnedVertexBuffer.bytes", "bytes",
                coverage ? CoverageCostStatus.Unavailable : CoverageCostStatus.NotApplicable,
                !coverage ? "coverage is off" : lastCovered >= 0 ? "stale: the covered preparation's frame is no longer current"
                    : "no current Covered preparation in the last frame"));
        var arena = scene.CoverageArenaCapacityForTests;
        result.Add(CoverageCostMetric.Measured("shared.arena.preparations", "elements", arena.Preparations, Peak));
        result.Add(CoverageCostMetric.Measured("shared.arena.equipmentStages", "elements", arena.EquipmentStages, Peak));
        var motion = scene.CoverageMotionReserveForTests;
        result.Add(CoverageCostMetric.Measured("shared.motion.previousList", "elements", motion.List, Peak));
        result.Add(CoverageCostMetric.Measured("shared.motion.gpuReserve", "elements", motion.Gpu, Peak));
        if (motion.Buffer is { } buffer) result.Add(CoverageCostMetric.Measured("shared.motion.gpuReserve.bytes", "bytes", buffer.SizeInBytes, Peak));
        result.Add(CoverageCostMetric.Measured("shared.transition.rectangles", "elements", scene.CoverageTransitionForTests.Rectangles.Length, Peak));
        return result;
    }

    internal IReadOnlyList<KeyValuePair<string, int>> ReadRefusals()
    {
        var result = new List<KeyValuePair<string, int>>();
        for (int code = 0; code < declines.Length; code++)
        {
            if (declines[code] > 0) result.Add(new($"decline:{(CoverageFailureCode)code}", declines[code]));
            if (fallbacks[code] > 0) result.Add(new($"fallback:{(CoverageFailureCode)code}", fallbacks[code]));
        }
        return result;
    }

    void Stage(CoverageStageSpan span, bool entering)
    {
        long now = clock();
        switch (span)
        {
            case CoverageStageSpan.Skinning when entering: skinStart = now; break;
            case CoverageStageSpan.Skinning: skinTicks += now - skinStart; skinned++; break;
            case CoverageStageSpan.GeometryEvaluation when entering: geometryStart = now; break;
            case CoverageStageSpan.GeometryEvaluation: geometryTicks += now - geometryStart; evaluated++; break;
            case CoverageStageSpan.GeometryCertification when entering: geometryCertificationStart = now; break;
            case CoverageStageSpan.GeometryCertification:
                geometryCertificationTicks += now - geometryCertificationStart; geometryCertified++; break;
            case CoverageStageSpan.OrdinarySkinning when entering: ordinaryStart = now; break;
            case CoverageStageSpan.OrdinarySkinning: ordinaryTicks += now - ordinaryStart; break;
            case CoverageStageSpan.SkinnedUpload when entering: uploadStart = now; break;
            case CoverageStageSpan.SkinnedUpload: uploadTicks += now - uploadStart; break;
            case CoverageStageSpan.RenderCertification when entering: certificationStart = now; break;
            case CoverageStageSpan.RenderCertification: certificationTicks += now - certificationStart; certified++; break;
        }
    }

    void Resolution(bool entering)
    {
        if (entering) { resolutionBytesStart = GC.GetAllocatedBytesForCurrentThread(); resolutionStart = clock(); }
        else { resolutionTicks += clock() - resolutionStart; resolutionBytes += GC.GetAllocatedBytesForCurrentThread() - resolutionBytesStart; }
    }

    void Surface(bool entering)
    {
        if (entering) { surfaceBytesStart = GC.GetAllocatedBytesForCurrentThread(); surfaceStart = clock(); }
        else { surfaceTicks += clock() - surfaceStart; surfaceBytes += GC.GetAllocatedBytesForCurrentThread() - surfaceBytesStart; }
    }

    public void Dispose()
    {
        scene.CoverageStageObserverForTests = null;
        scene.CoverageAllocationObserverForTests = null;
        scene.CoverageSurfaceAllocationObserverForTests = null;
        foreach (var binding in bindings) binding.Dispose();
        cache.Dispose();
    }
}
