using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageRecessionPublicationGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Every_pass_draws_the_selected_surface_and_submitted_previous_surface()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var observed = new CoverageRecessionObservedDevice(gpu.GpuDevice);
        var fixture = new CoverageRecessionBodySizeFixture(4, 4);
        using var scene = new CoverageRecessionNativeScene(observed, fixture, 1, covered: true);
        var evidence = new List<CoveragePassEvidence>();
        scene.Scene.CoveragePassObserverForTests = evidence.Add;
        using var native = gpu.GpuDevice.Factory.CreateCommandList();
        using var recorded = new RecordingGpuCommandList(native)
        { CaptureBindings = true, CaptureInstancedBindings = true, CaptureComputeBindings = true, CapturePayloads = true };
        // First submission establishes point-shadow resources and the previous selected row.
        scene.Prepare(0); recorded.Begin(); scene.Render(recorded); recorded.End(); scene.Submission.Submit(native);
        CoverageSurfaceRange prior = scene.Owners[0].Request.Surface;
        evidence.Clear(); recorded.Clear();
        scene.Prepare(1); recorded.Begin(); scene.Render(recorded); recorded.End(); scene.Submission.Submit(native);
        gpu.GpuDevice.WaitForIdle();
        Assert.NotEmpty(evidence);
        foreach (string pass in new[] { "motion", "cascade", "point", "full", "visible" })
            Assert.Contains(evidence, e => e.Pass == pass);
        Assert.All(evidence, e =>
        {
            Assert.Equal(scene.Owners[0].Actor, e.Actor);
            Assert.Same(evidence[0].Surface, e.Surface);
            Assert.Same(evidence[0].Previous, e.Previous);
            Assert.Equal(fixture.Topology.Indices.Length, e.IndexCount);
        });
        CoverageSurfaceRange current = scene.Owners[0].Request.Surface;
        Assert.Same(prior.Current, current.Previous);
        Assert.Equal(prior.CurrentBase, current.PreviousBase);
        // Inspect actual native resource sets at the recorded draws, not only logical observer tokens.
        AssertDrawBindings(observed, recorded, scene, current, evidence.Count);
        var coveredPrograms = observed.GraphicsShaders.Where(s => s.Vertex.Contains("CoverageCurrent", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(coveredPrograms); Assert.All(coveredPrograms, p => Assert.True(p.Options.PreciseMath));
        Assert.True(observed.ComputeShaders.Count >= 5);
        Assert.All(observed.ComputeShaders.Where(s => s.Source.Contains("CovFrameWords", StringComparison.Ordinal)),
            p => Assert.True(p.Options.PreciseMath));
        CoverageRecessionComputeGpuTests.AssertSurface(gpu.GpuDevice, fixture, current, 1,
            Vector3.UnitZ, 1, Vector3.Zero);
        Assert.NotNull(scene.Scene.CoverageHistoryForTests(scene.Owners[0].Actor).SurfaceToken);
        output.WriteLine($"All-pass native publication, {gpu.Backend}, {gpu.GpuDevice.Capabilities.DeviceName}, fixture {fixture.FixtureHash}, table {fixture.TableHash}");
        scene.Submission.Dispose();
    }

    internal static void AssertDrawBindings(CoverageRecessionObservedDevice observed, RecordingGpuCommandList recorded,
        CoverageRecessionNativeScene scene, CoverageSurfaceRange current, int expectedDraws)
    {
        var pulling = recorded.Bindings.Where(b => observed.Pass(b) is not null).ToArray();
        Assert.True(expectedDraws > 0);
        Assert.Equal(expectedDraws, pulling.Length);
        foreach (string pass in new[] { "motion", "cascade", "point", "full", "visible" })
            Assert.Contains(pulling, draw => observed.Pass(draw) == pass);
        Assert.All(pulling, draw =>
        {
            observed.AssertPulling(recorded, draw, current);
            if (observed.Pass(draw) == "motion")
                Assert.Same(scene.Scene.SkinnedGpuMaterialSetAt(scene.Host), draw.Sets[1].Set);
            else if (observed.Pass(draw) is "full" or "visible")
                Assert.Same(scene.Scene.SkinnedOutlineMaterialSetAt(scene.Host), draw.Sets[1].Set);
        });
    }
}

/// <summary>Real runtime preparation and drawing shared by native correctness and the bounded cost unit.</summary>
internal sealed class CoverageRecessionNativeScene : IDisposable
{
    internal const int Width = 320, Height = 240;
    internal readonly Scene3D Scene;
    internal readonly GpuFrameSubmission Submission;
    internal readonly SkinnedHostCoverage[] Owners;
    internal readonly int ActorCount;
    internal readonly SkinnedMeshHandle Host;
    internal readonly Matrix4x4[] Palette = [Matrix4x4.Identity];
    internal readonly CoverageRecessionBodySizeFixture Fixture;
    readonly IGpuTexture color;
    readonly IGpuFramebuffer target;
    readonly CoverageSupportSurface support;
    readonly MeshHandle equipment;
    readonly bool covered;
    readonly RigidInstanceDraw[] draws = new RigidInstanceDraw[1], frozen = new RigidInstanceDraw[1];
    readonly CoverageEquipmentCommand[] memberships;
    readonly CoverageCommandPose[] commands = new CoverageCommandPose[1];

    internal CoverageRecessionNativeScene(IGpuDevice device, CoverageRecessionBodySizeFixture fixture, int actors, bool covered)
    {
        Fixture = fixture; this.covered = covered; ActorCount = actors;
        color = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(Width, Height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        target = device.Factory.CreateFramebuffer(null, color);
        Scene = new(device, target.Outputs);
        Submission = new(device); // F03. Default capacity, one scene-owned aggregate participant for all actors.
        Scene.Post.Starfield = false;
        Scene.Post.RenderScale = RenderScale.MatchViewport;
        Scene.FrustumCulling = false;
        Scene.UseGpuSkinning = true;
        Scene.ForceTemporalForTests = true;
        Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        Scene.Post.Quality.Shadows.PointShadows.MaxDynamicLightsPerFrame = 1;
        Host = Scene.LoadSkinnedMesh(fixture.Mesh, Scene.LoadTexture([240, 160, 80, 255], 1, 1));
        var glb = new SkinnedAttachmentGlbFixture();
        glb.Root.Remove("skins"); glb.MeshNode.Remove("skin");
        glb.Primitives[0]!["attributes"]!.AsObject().Remove("JOINTS_0");
        glb.Primitives[0]!["attributes"]!.AsObject().Remove("WEIGHTS_0");
        var panel = CoverageFixture.CurvedPanel(false);
        glb.Accessors[0]!["bufferView"] = glb.AddView(MemoryMarshal.AsBytes(panel.RigidPositions.AsSpan()).ToArray());
        glb.Accessors[0]!["count"] = panel.RigidPositions.Length;
        glb.Accessors[0]!["min"] = new JsonArray(-1, -1, .02f);
        glb.Accessors[0]!["max"] = new JsonArray(1, 1, .2f);
        glb.Primitives[0]!["indices"] = glb.Accessors.Count;
        glb.Accessors.Add(new JsonObject
        {
            ["bufferView"] = glb.AddView(MemoryMarshal.AsBytes(panel.RigidIndices.AsSpan()).ToArray()),
            ["componentType"] = 5125,
            ["count"] = panel.RigidIndices.Length,
            ["type"] = "SCALAR"
        });
        byte[] bytes = glb.Build();
        var source = GltfLoader.LoadCoverageSource(bytes, "synthetic-support");
        support = CoverageSupportSurface.Compile(source, [new(0, 0, 0, 4)], Matrix4x4.Identity);
        equipment = Scene.LoadMesh(GltfLoader.LoadPartsWithMaterials(bytes, "synthetic-support")[0].Mesh);
        memberships = [new(1, support)];
        Owners = new SkinnedHostCoverage[covered ? actors : 0];
        for (int i = 0; i < Owners.Length; i++)
        {
            Owners[i] = Scene.CreateHostCoverage(fixture.Skeleton, [Host], fixture.Table);
            Owners[i].BindActor(MotionKey.From((ulong)i + 1));
            Owners[i].ReserveEquipment(1);
        }
    }

    internal void Prepare(int frame, float transition = 1, Matrix4x4? coverageWorld = null)
    {
        Submission.Begin(); Scene.BeginFrame(Submission);
        Queue(frame, transition, coverageWorld);
        Scene.PrepareFrame(Width, Height);
    }

    internal void Queue(int frame, float transition = 1, Matrix4x4? coverageWorld = null)
    {
        Scene.AddLight(new(0, 1, 1), Color.White, 10, 1, LightShadow.Dynamic);
        for (int i = 0; i < ActorCount; i++)
        {
            MotionKey key = MotionKey.From((ulong)i + 1);
            Matrix4x4 model = Matrix4x4.CreateTranslation((i % 8 - (ActorCount > 1 ? 3.5f : 0)) * .2f, i / 8 * .2f, 0);
            Matrix4x4 equipmentWorld = coverageWorld ?? model;
            draws[0] = new(equipment, equipmentWorld);
            SkinnedCoverageFrame token = default;
            if (covered)
            {
                var owner = Owners[i];
                if (!Scene.TryStageCoverageEquipment(owner.Actor, (ulong)i + 1, memberships, draws, frozen, out var failure))
                    throw new CoverageFrameStateException(failure);
                commands[0] = new(1, (ulong)i + 1, owner.Topology, 0, support, equipmentWorld, equipmentWorld,
                    -2, 2, .125f, .02f, 1, transition);
                bool selected = owner.TryPrepare(Palette, model, new(frame % 3 == 2 ? 30ul : frame % 2 == 0 ? 10ul : 20ul),
                    commands, owner.Actor, out token, out failure);
                if (!selected && failure.Code != CoverageFailureCode.TableHole) throw new CoverageFrameStateException(failure);
            }
            Scene.Draw(covered ? frozen[0] : draws[0]);
            var group = Scene.BeginMeshOutline(Color.White, 2);
            var draw = new SkinnedInstanceDraw(Host, model) { Motion = key, Coverage = token };
            Scene.DrawSkinned(draw, Palette);
            Scene.DrawSkinnedOutline(group, draw, Palette);
        }
    }

    internal void Render(IGpuCommandList list) => Scene.RenderInternal(list, Width, Height, target);
    internal void AssertPublished()
    {
        if (!covered) return;
        foreach (var owner in Owners)
        {
            Assert.True(owner.Request.Published, Scene.CoverageFailureFor(owner.Actor).Code.ToString());
            Assert.NotNull(Scene.CoverageHistoryForTests(owner.Actor).SurfaceToken);
        }
    }
    public void Dispose()
    {
        Submission.Dispose();
        foreach (var owner in Owners) owner.Dispose();
        Scene.Dispose(); target.Dispose(); color.Dispose();
    }
}
