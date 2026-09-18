using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// WHEN the point-shadow atlas is allocated, reshaped and released, device-free. Every one of those is frame
/// BOUNDARY work, so the observable here is a one-frame delay: the frame that first asks for a map renders
/// without one and the next frame carries it.
/// <para>
/// That delay is the whole point rather than a wart. An allocation is two textures, a framebuffer, four pipelines
/// and a rebuild of every material set in the scene, and the rebuild carries a <c>WaitForIdle</c>. Doing it with
/// the frame's command list open stalls the device mid-recording and swaps the sets the model pass is about to
/// bind. The cascade atlas has taken exactly this route since it grew a live reconfigure, and these tests are
/// that file's tests one feature over.
/// </para>
/// </summary>
public sealed class PointShadowReconfigureTests
{
    [Fact]
    public void TheFirstRequestRendersUnshadowedAndTheFrameAfterItCarriesTheAtlas()
    {
        using var rig = new ReconfigureRig();

        // Nothing has asked, so nothing is allocated. This is the state a game that never uses point shadows
        // stays in for its whole life.
        Assert.Null(rig.Scene.PointShadowTexture);
        Assert.False(rig.Scene.ResolvedPointShadows.Enabled);

        rig.RenderFrame(LightShadow.Static(1));

        // The frame recorded what it wanted and rendered without it. No atlas, no light carrying a map.
        Assert.Null(rig.Scene.PointShadowTexture);
        Assert.False(rig.Scene.ResolvedPointShadows.Enabled);
        Assert.Equal(0, rig.Scene.PointShadowedLights);
        Assert.Equal(0, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);

        rig.RenderFrame(LightShadow.Static(1));

        Assert.NotNull(rig.Scene.PointShadowTexture);
        Assert.Equal(new PointShadowResolution(true, 256, 8, false, null), rig.Scene.ResolvedPointShadows);
        Assert.Equal(1, rig.Scene.PointShadowedLights);
        Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
    }

    [Fact]
    public void AFrameThatAsksForNothingAllocatesNothingHoweverManyOfThemThereAre()
    {
        using var rig = new ReconfigureRig();

        for (int i = 0; i < 4; i++) rig.RenderFrame(LightShadow.None);

        Assert.Null(rig.Scene.PointShadowTexture);
        Assert.False(rig.Scene.ResolvedPointShadows.Enabled);
        Assert.False(rig.Scene.ResolvedPointShadows.Degraded);
        Assert.Equal("", rig.Scene.ResolvedPointShadows.Reason);
    }

    [Fact]
    public void MutatingTheSettingsInPlaceIsPickedUpAtTheNextBoundary()
    {
        using var rig = new ReconfigureRig();
        rig.RenderTwoFrames(LightShadow.Static(1));
        IGpuTexture? first = rig.Scene.PointShadowTexture;

        rig.Settings.PointShadows.FaceResolution = 128;
        rig.RenderFrame(LightShadow.Static(1));

        // The field is the documented way to tune this, so it has to reach the atlas without a request call.
        Assert.Equal(new PointShadowResolution(true, 128, 8, false, null), rig.Scene.ResolvedPointShadows);
        Assert.NotSame(first, rig.Scene.PointShadowTexture);
        // A new texture means the old rows went with it, so the light re-renders rather than sampling a row that
        // no longer exists.
        Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(1, rig.Scene.PointShadowedLights);
    }

    [Fact]
    public void ARequestedBudgetIsClonedAndAppliedAtTheNextBoundaryAndNotBefore()
    {
        using var rig = new ReconfigureRig();
        rig.RenderTwoFrames(LightShadow.Static(1));
        var requested = new PointShadowSettings { FaceResolution = 128, MaxShadowedLights = 4 };

        rig.Scene.RequestPointShadowSettings(requested);

        Assert.Equal(new PointShadowResolution(true, 256, 8, false, null), rig.Scene.ResolvedPointShadows);

        // Mutating the caller's object after the call must not reach the scene, which is what the clone buys.
        requested.FaceResolution = 1024;
        rig.RenderFrame(LightShadow.Static(1));

        Assert.Equal(new PointShadowResolution(true, 128, 4, false, null), rig.Scene.ResolvedPointShadows);
        Assert.NotSame(requested, rig.Settings.PointShadows);
    }

    [Fact]
    public void DisablingReleasesTheAtlasAndEnablingBringsItBack()
    {
        using var rig = new ReconfigureRig();
        rig.RenderTwoFrames(LightShadow.Static(1));
        Assert.NotNull(rig.Scene.PointShadowTexture);

        rig.Scene.RequestPointShadowSettings(new PointShadowSettings { Enabled = false });
        rig.RenderFrame(LightShadow.Static(1));

        Assert.Null(rig.Scene.PointShadowTexture);
        Assert.Equal(new PointShadowResolution(false, 0, 0, false, null), rig.Scene.ResolvedPointShadows);
        Assert.Equal(0, rig.Scene.PointShadowedLights);

        // And back on: one frame to ask, one to have it, exactly as the first time.
        rig.Scene.RequestPointShadowSettings(new PointShadowSettings());
        rig.RenderFrame(LightShadow.Static(1));
        Assert.Null(rig.Scene.PointShadowTexture);
        rig.RenderFrame(LightShadow.Static(1));
        Assert.NotNull(rig.Scene.PointShadowTexture);
        Assert.Equal(1, rig.Scene.PointShadowedLights);
    }

    /// <summary>
    /// A game's quality menu calls <see cref="Scene3D.RequestShadowMapDetail"/> and nothing else, so the point
    /// atlas has to ride it. Without this the point half of a shadow setting would need a restart, which is the
    /// same bug the cascade half already had and fixed.
    /// </summary>
    [Fact]
    public void RequestShadowMapDetailCarriesThePointProfile()
    {
        using var rig = new ReconfigureRig();
        rig.RenderTwoFrames(LightShadow.Static(1));

        rig.Scene.RequestShadowMapDetail(ShadowMapDetail.High);
        rig.RenderFrame(LightShadow.Static(1));

        Assert.Equal(new PointShadowResolution(true, 384, 12, false, null), rig.Scene.ResolvedPointShadows);
        Assert.Equal(1, rig.Scene.PointShadowedLights);
        Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);

        rig.Scene.RequestShadowMapDetail(ShadowMapDetail.Low);
        rig.RenderFrame(LightShadow.Static(1));

        Assert.Null(rig.Scene.PointShadowTexture);
        Assert.False(rig.Scene.ResolvedPointShadows.Enabled);
        Assert.Equal(0, rig.Scene.PointShadowedLights);

        rig.Scene.RequestShadowMapDetail(ShadowMapDetail.Default);
        rig.RenderFrame(LightShadow.Static(1));
        rig.RenderFrame(LightShadow.Static(1));
        Assert.Equal(new PointShadowResolution(true, 256, 8, false, null), rig.Scene.ResolvedPointShadows);
    }

    /// <summary>
    /// THE REFUSAL IS ANSWERED ONCE. A device that cannot allocate the wanted layout is not going to start being
    /// able to, so retrying every boundary costs two textures, four pipelines and the whole transaction a frame
    /// forever, for the same answer and with nothing on screen saying so. The previous layout carries on
    /// rendering, the resolution says it is degraded and why, and the log says it once.
    /// </summary>
    [Fact]
    public void ARefusedLayoutKeepsThePreviousOneAndIsNotRetried()
    {
        using var rig = new ReconfigureRig();
        rig.RenderTwoFrames(LightShadow.Static(1));
        IGpuTexture? live = rig.Scene.PointShadowTexture;

        rig.FailTheNextResourceSetCreate();
        rig.Scene.RequestPointShadowSettings(new PointShadowSettings { FaceResolution = 512 });
        rig.RenderFrame(LightShadow.Static(1));

        PointShadowResolution refused = rig.Scene.ResolvedPointShadows;
        Assert.True(refused.Degraded);
        Assert.Contains("512", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("256", refused.Reason, StringComparison.Ordinal);
        // The scene kept what it had, so the light it was already shadowing is still shadowed.
        Assert.Same(live, rig.Scene.PointShadowTexture);
        Assert.True(refused.Enabled);
        Assert.Equal(256, refused.FaceResolution);
        Assert.Equal(1, rig.Scene.PointShadowedLights);
        Assert.Single(rig.Logger.Errors);

        int textures = rig.TextureCount;
        rig.RenderFrame(LightShadow.Static(1));
        rig.RenderFrame(LightShadow.Static(1));

        Assert.Equal(textures, rig.TextureCount);
        Assert.Equal(refused, rig.Scene.ResolvedPointShadows);
        Assert.Single(rig.Logger.Errors);
        Assert.Equal(1, rig.Scene.PointShadowedLights);
    }

    [Fact]
    public void ADifferentLayoutAfterARefusalIsAttemptedForReal()
    {
        using var rig = new ReconfigureRig();
        rig.RenderTwoFrames(LightShadow.Static(1));
        rig.FailTheNextResourceSetCreate();
        rig.Scene.RequestPointShadowSettings(new PointShadowSettings { FaceResolution = 512 });
        rig.RenderFrame(LightShadow.Static(1));
        Assert.True(rig.Scene.ResolvedPointShadows.Degraded);

        rig.StopFailing();
        rig.Scene.RequestPointShadowSettings(new PointShadowSettings { FaceResolution = 128 });
        rig.RenderFrame(LightShadow.Static(1));

        Assert.Equal(new PointShadowResolution(true, 128, 8, false, null), rig.Scene.ResolvedPointShadows);
        Assert.Equal(1, rig.Scene.PointShadowedLights);
    }

    /// <summary>
    /// A LIGHT ARRIVING COSTS ONE REBUILD, WHOEVER ASKS FIRST. The requests are offered rows nearest first, so a
    /// newcomer standing closest to the eye asks before every incumbent behind it, and a cache that handed it a
    /// row on that first ask would evict a light that was about to re-ask. Each displaced incumbent then evicts
    /// the next, and a walk through a town with more lanterns than rows re-renders half the atlas every time the
    /// nearest light changes.
    /// <para>
    /// The rebuild budget is opened to the whole atlas here on purpose: with the shipped budget of two the
    /// cascade would be capped at two rebuilds and read as ordinary work. The assertion is ONE.
    /// </para>
    /// </summary>
    [Fact]
    public void AnArrivingLightTakesTheVacatedRowAndRebuildsNothingElse()
    {
        using var rig = new ReconfigureRig();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 8;
        Vector3 eye = rig.Scene.Camera.Eye;
        const long Newcomer = 99;

        // Eight lights on one line away from the eye, nearest first, so their queue order is their rank order.
        void Incumbents(Scene3D scene, int skip)
        {
            scene.Draw(rig.Mesh, Matrix4x4.Identity);
            for (int light = 0; light < 8; light++)
            {
                if (light == skip) continue;
                scene.AddLight(eye + new Vector3(0f, 0f, 10f + light), Color.White, 4f, 1f,
                    LightShadow.Static(light));
            }
        }

        rig.RenderFrame(s => Incumbents(s, -1));   // asks for the atlas
        rig.RenderFrame(s => Incumbents(s, -1));   // and fills every row of it
        Assert.Equal(8, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        rig.RenderFrame(s => Incumbents(s, -1));
        Assert.Equal(0, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(8, rig.Scene.PointShadowedLights);

        // Light 3 stops being queued and the newcomer stands NEAREST, so it is the first request offered a row.
        rig.RenderFrame(s =>
        {
            s.AddLight(eye + new Vector3(0f, 0f, 5f), Color.White, 4f, 1f, LightShadow.Static(Newcomer));
            Incumbents(s, skip: 3);
        });

        Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(8, rig.Scene.PointShadowedLights);
        Assert.Equal(8, rig.Scene.LastShadowPassDiagnostics.PointSlotsInUse);
    }

    /// <summary>
    /// A DYNAMIC LIGHT PAST THE BUDGET SAMPLES NOTHING, BECAUSE ITS ROW IS NOT ITS OWN. A dynamic light carries no
    /// key, so the scene keys it by its place in the light queue, and that number belongs to somebody else the
    /// moment one of them expires and the rest shift down. Eight of them with a dynamic budget of four: the four
    /// nearest are drawn, the other four are not, and a row kept from an earlier frame would let one of those four
    /// match a row another light drew and cast that light's shadow from its own position, indefinitely.
    /// <para>
    /// The lights MOVE between the two frames, which is the whole reason they are dynamic, so the four nearest are
    /// a different four and the inherited keys land on lights the budget cannot reach.
    /// </para>
    /// </summary>
    [Fact]
    public void ADynamicLightPastTheBudgetPublishesNoSlotAfterTheQueueShifts()
    {
        using var rig = new ReconfigureRig();
        Vector3 eye = rig.Scene.Camera.Eye;
        Assert.Equal(4, rig.Settings.PointShadows.MaxDynamicLightsPerFrame);

        void Queue(Scene3D scene, Func<int, float> distance, int count)
        {
            scene.Draw(rig.Mesh, Matrix4x4.Identity);
            for (int i = 0; i < count; i++)
                scene.AddLight(eye + new Vector3(0f, 0f, distance(i)), Color.White, 4f, 1f, LightShadow.Dynamic);
        }

        // Eight dynamics, nearest first, so the four the budget draws are queue indices 0 to 3.
        rig.RenderFrame(s => Queue(s, i => 10f + i, 8));
        rig.RenderFrame(s => Queue(s, i => 10f + i, 8));

        Assert.Equal(4, rig.Scene.LastShadowPassDiagnostics.PointDynamicRenders);
        Assert.Equal(4, rig.Scene.PointShadowedLights);

        // One expires and the rest shift down a place, having moved: the four nearest are now queue indices 3 to
        // 6, so keys 0, 1 and 2 are held by lights the budget will not draw and whose rows another light filled.
        rig.RenderFrame(s => Queue(s, i => i < 3 ? 40f + i : 10f + i, 7));

        Assert.Equal(4, rig.Scene.LastShadowPassDiagnostics.PointDynamicRenders);
        Assert.Equal(4, rig.Scene.PointShadowedLights);
        Assert.Equal(0, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
    }

    /// <summary>
    /// A NEW TEXTURE OF THE SAME SHAPE IS STILL A NEW TEXTURE. Changing only the face size keeps the row count,
    /// so the slot cache survives the reshape with its owners intact, and nothing about the OWNERS says the rows
    /// they point at were freed with the texture under them. An R32F allocation reads as zero, which the compare
    /// takes as fully occluded, so a static light left reading its old row goes black rather than merely stale.
    /// <para>
    /// The rebuild budget is one and two lights are shadowed, so only one of them can be re-drawn on the frame the
    /// reshape lands. The other must report NO slot until its turn comes: a count of two here is a light sampling
    /// a texture nothing has drawn into.
    /// </para>
    /// </summary>
    [Fact]
    public void AReshapedAtlasOfTheSameRowCountLeavesEveryRowUndrawn()
    {
        using var rig = new ReconfigureRig();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 1;
        Vector3 eye = rig.Scene.Camera.Eye;

        void Pair(Scene3D scene)
        {
            scene.Draw(rig.Mesh, Matrix4x4.Identity);
            scene.AddLight(eye + new Vector3(0f, 0f, 10f), Color.White, 4f, 1f, LightShadow.Static(1));
            scene.AddLight(eye + new Vector3(0f, 0f, 20f), Color.White, 4f, 1f, LightShadow.Static(2));
        }

        for (int i = 0; i < 4; i++) rig.RenderFrame(Pair);
        Assert.Equal(new PointShadowResolution(true, 256, 8, false, null), rig.Scene.ResolvedPointShadows);
        Assert.Equal(2, rig.Scene.PointShadowedLights);
        Assert.Equal(0, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);

        // The same eight rows at a bigger face, which is a whole new texture and a whole new set of empty rows.
        rig.Settings.PointShadows.FaceResolution = 384;
        rig.RenderFrame(Pair);

        Assert.Equal(new PointShadowResolution(true, 384, 8, false, null), rig.Scene.ResolvedPointShadows);
        Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(1, rig.Scene.PointShadowedLights);

        rig.RenderFrame(Pair);

        Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(2, rig.Scene.PointShadowedLights);
    }

    /// <summary>
    /// A LIGHT THAT IS DIRTY EVERY FRAME MUST NOT STARVE THE OTHERS. One static light has a rigid caster inside it
    /// whose matrix changes every frame, which makes its signature change every frame, and the rebuild budget is
    /// ONE. Ranking the dirty rows in request order would hand that one light the whole budget for ever, and the
    /// other two would sit on rows nothing had drawn into, publishing no slot and lighting nothing, for the whole
    /// life of the scene.
    /// <para>
    /// The ranking is stalest first with a never-drawn row sorting ahead of every drawn one, so the always-dirty
    /// light takes its turn and then goes to the back. Three lights and a budget of one means three frames.
    /// </para>
    /// </summary>
    [Fact]
    public void AnAlwaysDirtyStaticLightDoesNotStarveTheOthersOfTheRebuildBudget()
    {
        using var rig = new ReconfigureRig();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 1;
        Vector3 eye = rig.Scene.Camera.Eye;
        Vector3 jittering = eye + new Vector3(0f, 0f, 10f);

        void Lights(Scene3D scene, int frame)
        {
            // The caster stands inside the nearest light and nowhere near the other two, and it moves a hair every
            // frame, which is a changed matrix and so a changed signature.
            scene.Draw(rig.Mesh, Matrix4x4.CreateTranslation(jittering + new Vector3(frame * 0.001f, 0f, 0f)));
            scene.AddLight(jittering, Color.White, 6f, 1f, LightShadow.Static(1));
            scene.AddLight(eye + new Vector3(0f, 0f, 30f), Color.White, 6f, 1f, LightShadow.Static(2));
            scene.AddLight(eye + new Vector3(0f, 0f, 50f), Color.White, 6f, 1f, LightShadow.Static(3));
        }

        int frame = 0;
        rig.RenderFrame(s => Lights(s, frame++));   // asks for the atlas, renders unshadowed
        var shadowed = new int[4];
        for (int i = 0; i < 4; i++)
        {
            rig.RenderFrame(s => Lights(s, frame++));
            shadowed[i] = rig.Scene.PointShadowedLights;
            Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        }

        // One light a frame, in a bounded three, and then the always-dirty one takes its turn again without
        // costing anybody theirs.
        Assert.Equal(new[] { 1, 2, 3, 3 }, shadowed);

        // Still one rebuild a frame after that, which is the jittering caster keeping its own light dirty: a zero
        // here would say the caster had stopped moving and the case had stopped testing anything.
        rig.RenderFrame(s => Lights(s, frame++));
        Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(3, rig.Scene.PointShadowedLights);
    }

    [Fact]
    public void ARequestAfterDisposalThrows()
    {
        using var rig = new ReconfigureRig();
        rig.DisposeScene();

        Assert.Throws<ObjectDisposedException>(() =>
            rig.Scene.RequestPointShadowSettings(new PointShadowSettings()));
    }

    [Fact]
    public void ANullRequestThrows()
    {
        using var rig = new ReconfigureRig();

        Assert.Throws<ArgumentNullException>(() => rig.Scene.RequestPointShadowSettings(null!));
    }

    /// <summary>A scene over the fake device that can render whole frames, with one triangle in it and one point
    /// light over it. Device-free: nothing here reads a texel, it all reads which resources exist.</summary>
    sealed class ReconfigureRig : IDisposable
    {
        static readonly Vector3 LightAt = new(0f, 2f, 0f);
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;
        readonly MeshHandle _mesh;
        bool _sceneDisposed;

        internal ReconfigureRig()
        {
            Device = new FakeGpuDevice();
            Factory = (FakeGpuResourceFactory)Device.Factory;
            _targetTexture = Factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = Factory.CreateFramebuffer(null, _targetTexture);
            // The key light's own atlas is not what any of this measures, and leaving it off keeps a detail
            // request's cascade half from allocating beside the point half.
            Settings = new ShadowSettings { Mode = ShadowMode.Off };
            Logger = new RecordingLogger();
            Scene = new Scene3D(Device, _target.Outputs, Settings, Logger);
            _mesh = Scene.LoadMesh(MeshPrimitives.Box(1f));
        }

        internal FakeGpuDevice Device { get; }
        internal FakeGpuResourceFactory Factory { get; }
        internal ShadowSettings Settings { get; }
        internal RecordingLogger Logger { get; }
        internal Scene3D Scene { get; }
        internal int TextureCount => Factory.Textures.Count;

        /// <summary>The box the one-light frames draw, for a case that wants to place its own copies of it.</summary>
        internal MeshHandle Mesh => _mesh;

        internal void RenderFrame(LightShadow shadow) => RenderFrame(scene =>
        {
            scene.Draw(_mesh, Matrix4x4.Identity);
            scene.AddLight(LightAt, Color.White, 10f, 1f, shadow);
        });

        /// <summary>Render one whole frame of whatever <paramref name="describe"/> queues, for the cases that need
        /// more than the one light and one box the overload above draws.</summary>
        internal void RenderFrame(Action<Scene3D> describe)
        {
            Scene.Begin();
            describe(Scene);
            Scene.PrepareFrame();
            using IGpuCommandList commands = Factory.CreateCommandList();
            commands.Begin();
            Scene.RenderInternal(commands, 16, 16, _target);
            commands.End();
        }

        /// <summary>The two frames it takes to go from a first request to a live atlas: one to ask, one to have
        /// it. Every case that starts from "already shadowing" opens with this.</summary>
        internal void RenderTwoFrames(LightShadow shadow)
        {
            RenderFrame(shadow);
            RenderFrame(shadow);
        }

        // Sticky until something creates a set successfully, which is what a device that cannot serve this layout
        // behaves like. The pass's slot ring is the last thing its constructor builds, so the refusal lands after
        // the atlas textures and all four pipelines exist.
        internal void FailTheNextResourceSetCreate() => Factory.ThrowOnResourceSetCreate = Factory.ResourceSets.Count + 1;

        internal void StopFailing() => Factory.ThrowOnResourceSetCreate = 0;

        internal void DisposeScene()
        {
            if (_sceneDisposed) return;
            Scene.Dispose();
            _sceneDisposed = true;
        }

        public void Dispose()
        {
            DisposeScene();
            _target.Dispose();
            _targetTexture.Dispose();
            Device.Dispose();
        }
    }
}
